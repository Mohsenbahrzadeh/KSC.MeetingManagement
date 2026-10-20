using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KSC.MeetingManagement.Application.Media;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Reports;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Media;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

/// <summary>TASK-053: attendance built only from validated media-server events.</summary>
public sealed class PresenceApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Secret = "0123456789abcdef0123456789abcdef-secret";
    private const string ApiKey = "APIkey1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly WebApplicationFactory<Program> _baseFactory;
    private readonly Clock _clock = new(new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly Tenant _tenant = new(Guid.NewGuid(), "a", "A", "UTC");
    private readonly Tenant _tenantB = new(Guid.NewGuid(), "b", "B", "UTC");
    private readonly Guid _host = Guid.NewGuid();
    private readonly Guid _auditor = Guid.NewGuid();
    private readonly Guid _participant = Guid.NewGuid();
    private readonly Guid _auditorB = Guid.NewGuid();
    private readonly HttpClient _client;
    private readonly DateTimeOffset _t0 = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
    private int _eventCounter;

    public PresenceApiTests(WebApplicationFactory<Program> factory)
    {
        _baseFactory = factory;
        _client = Create(configured: true);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private HttpClient Create(bool configured)
    {
        var app = _baseFactory.WithWebHostBuilder(b =>
        {
            if (configured)
            {
                b.UseSetting("Media:ServerUrl", "wss://media.example.org");
                b.UseSetting("Media:ApiKey", ApiKey);
                b.UseSetting("Media:ApiSecret", Secret);
            }

            b.ConfigureServices(s =>
            {
                s.UseInMemoryKscDatabase(Guid.NewGuid().ToString());
                s.AddSingleton<TimeProvider>(_clock);
                s.AddAuthentication(TestAuthHandler.Scheme)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
            });
        });

        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenant);
        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenantB);
        var members = app.Services.GetRequiredService<InMemoryMembershipStore>();
        members.Add(new Membership(_tenant.Id, _host, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenant.Id, _auditor, [Roles.Auditor]));
        members.Add(new Membership(_tenant.Id, _participant, [Roles.Participant]));
        members.Add(new Membership(_tenantB.Id, _auditorB, [Roles.Auditor]));
        return app.CreateClient(new() { AllowAutoRedirect = false });
    }

    private Task<HttpResponseMessage> Api(HttpMethod method, string url, Tenant tenant, Guid user, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Tenant-Id", tenant.Id.ToString());
        request.Headers.Add("X-Test-User", user.ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _client.SendAsync(request);
    }

    private async Task<MeetingDto> Meeting()
    {
        var start = _t0;
        var response = await Api(HttpMethod.Post, "/api/v1/meetings", _tenant, _host, new { title = "x", mode = "Online", start, end = start.AddHours(1), allowConflicts = true });
        return (await response.Content.ReadFromJsonAsync<MeetingDto>(Json))!;
    }

    private string Sign(byte[] body, string secret = Secret, string iss = ApiKey, long? exp = null, bool withHash = true, long? nbf = null, DateTimeOffset? now = null)
    {
        var at = now ?? _clock.Now;
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = iss,
            ["nbf"] = nbf ?? at.ToUnixTimeSeconds(),
            ["exp"] = exp ?? at.AddMinutes(5).ToUnixTimeSeconds(),
        };
        if (withHash)
        {
            payload["sha256"] = Convert.ToBase64String(SHA256.HashData(body));
        }

        string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var head = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = "HS256", ["typ"] = "JWT" }));
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{head}.{claims}.{B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes($"{head}.{claims}")))}";
    }

    private byte[] EventBody(string type, string room, string? identity = null, string? name = null, DateTimeOffset? at = null, string? id = null)
    {
        // Events arrive after they happened: time moves forward to the event (the service ignores claims from the future).
        if (at is { } eventTime && eventTime > _clock.Now)
        {
            _clock.Now = eventTime;
        }

        var evt = new Dictionary<string, object?>
        {
            ["event"] = type,
            ["id"] = id ?? $"EV_{++_eventCounter}",
            ["createdAt"] = (at ?? _clock.Now).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["room"] = new Dictionary<string, object?> { ["sid"] = "RM_1", ["name"] = room },
        };
        if (identity is not null)
        {
            evt["participant"] = new Dictionary<string, object?> { ["sid"] = "PA_1", ["identity"] = identity, ["name"] = name ?? "مهمان" };
        }

        return JsonSerializer.SerializeToUtf8Bytes(evt);
    }

    private async Task<(HttpStatusCode Status, string Outcome)> Webhook(byte[] body, string? authorization = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media/webhook") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/webhook+json");
        request.Headers.TryAddWithoutValidation("Authorization", authorization ?? Sign(body));
        var response = await (client ?? _client).SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.IsSuccessStatusCode ? JsonDocument.Parse(text).RootElement.GetProperty("status").GetString()! : text);
    }

    private string Room(Guid meetingId, Tenant? tenant = null) => MediaAccessService.RoomName((tenant ?? _tenant).Id, meetingId);

    private static string Member(Guid id) => $"u-{id:N}";

    private async Task<PresenceSummary> Presence(Guid meetingId) =>
        (await (await Api(HttpMethod.Get, $"/api/v1/meetings/{meetingId}/presence", _tenant, _auditor)).Content.ReadFromJsonAsync<PresenceSummary>(Json))!;

    [Fact]
    public async Task Join_and_leave_events_give_the_time_spent_in_the_room()
    {
        var meeting = await Meeting();
        var room = Room(meeting.Id);
        var joined = _t0.AddMinutes(1);

        var join = await Webhook(EventBody("participant_joined", room, Member(_participant), "علی", at: joined));
        var left = await Webhook(EventBody("participant_left", room, Member(_participant), at: joined.AddMinutes(25)));
        var summary = await Presence(meeting.Id);

        Assert.Equal((HttpStatusCode.OK, "recorded"), join);
        Assert.Equal((HttpStatusCode.OK, "recorded"), left);
        var stay = Assert.Single(summary.Stays);
        Assert.Equal((Member(_participant), "علی", joined, joined.AddMinutes(25), 25.0), (stay.Identity, stay.DisplayName, stay.JoinedAtUtc, stay.LeftAtUtc, stay.Minutes));
        Assert.Equal((1, 25.0), (summary.People, summary.TotalMinutes));
    }

    [Fact]
    public async Task A_repeated_delivery_of_the_same_event_changes_nothing()
    {
        var meeting = await Meeting();
        var body = EventBody("participant_joined", Room(meeting.Id), Member(_participant), id: "EV_same");

        var first = await Webhook(body);
        var second = await Webhook(body);
        var third = await Webhook(body);

        Assert.Equal("recorded", first.Outcome);
        Assert.Equal("duplicate", second.Outcome);
        Assert.Equal("duplicate", third.Outcome);
        Assert.Single((await Presence(meeting.Id)).Stays);
    }

    [Fact]
    public async Task A_second_join_for_someone_already_inside_is_not_a_second_stay_but_a_new_stay_follows_a_leave()
    {
        var meeting = await Meeting();
        var room = Room(meeting.Id);
        var person = Member(_participant);

        await Webhook(EventBody("participant_joined", room, person, at: _t0));
        await Webhook(EventBody("participant_joined", room, person, at: _t0.AddMinutes(1)));
        Assert.Single((await Presence(meeting.Id)).Stays);

        await Webhook(EventBody("participant_left", room, person, at: _t0.AddMinutes(10)));
        await Webhook(EventBody("participant_joined", room, person, at: _t0.AddMinutes(20)));
        await Webhook(EventBody("participant_left", room, person, at: _t0.AddMinutes(30)));
        var summary = await Presence(meeting.Id);

        Assert.Equal(2, summary.Stays.Count);
        Assert.Equal(1, summary.People);
        Assert.Equal(20.0, summary.TotalMinutes);
    }

    [Fact]
    public async Task A_leave_without_a_known_stay_is_ignored_and_a_clock_that_runs_backwards_never_gives_negative_time()
    {
        var meeting = await Meeting();
        var room = Room(meeting.Id);

        await Webhook(EventBody("participant_left", room, Member(_participant), at: _t0));
        Assert.Empty((await Presence(meeting.Id)).Stays);

        await Webhook(EventBody("participant_joined", room, Member(_participant), at: _t0.AddMinutes(10)));
        await Webhook(EventBody("participant_left", room, Member(_participant), at: _t0.AddMinutes(5)));
        var stay = Assert.Single((await Presence(meeting.Id)).Stays);

        Assert.Equal(0, stay.Minutes);
        Assert.Equal(stay.JoinedAtUtc, stay.LeftAtUtc);
    }

    [Fact]
    public async Task Room_finished_closes_every_open_stay()
    {
        var meeting = await Meeting();
        var room = Room(meeting.Id);
        await Webhook(EventBody("participant_joined", room, Member(_participant), at: _t0));
        await Webhook(EventBody("participant_joined", room, $"g-{Guid.NewGuid():N}", "مهمان", at: _t0.AddMinutes(5)));

        await Webhook(EventBody("room_finished", room, at: _t0.AddMinutes(30)));
        var summary = await Presence(meeting.Id);

        Assert.All(summary.Stays, s => Assert.Equal(_t0.AddMinutes(30), s.LeftAtUtc));
        Assert.Equal(55.0, summary.TotalMinutes);
    }

    [Fact]
    public async Task Events_with_a_bad_signature_a_tampered_body_or_stale_credentials_are_refused()
    {
        var meeting = await Meeting();
        var body = EventBody("participant_joined", Room(meeting.Id), Member(_participant));
        var other = EventBody("participant_joined", Room(meeting.Id), Member(_host));
        var now = _clock.Now;

        var results = new[]
        {
            await Webhook(body, Sign(body, secret: "another-secret-another-secret-another")),
            await Webhook(body, Sign(body, iss: "someone-else")),
            await Webhook(other, Sign(body)),
            await Webhook(body, Sign(body, exp: now.AddMinutes(-5).ToUnixTimeSeconds(), nbf: now.AddMinutes(-10).ToUnixTimeSeconds())),
            await Webhook(body, Sign(body, nbf: now.AddMinutes(10).ToUnixTimeSeconds())),
            await Webhook(body, Sign(body, withHash: false)),
            await Webhook(body, "not-a-jwt"),
            await Webhook(body, string.Empty),
        };

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Unauthorized, r.Status));
        Assert.Empty((await Presence(meeting.Id)).Stays);
    }

    [Fact]
    public async Task An_unsigned_token_with_alg_none_is_refused()
    {
        var meeting = await Meeting();
        var body = EventBody("participant_joined", Room(meeting.Id), Member(_participant));
        string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var claims = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["iss"] = ApiKey, ["sha256"] = Convert.ToBase64String(SHA256.HashData(body)) });
        var unsigned = $"{B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = "none" }))}.{B64(claims)}.";

        var result = await Webhook(body, unsigned);

        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
    }

    [Fact]
    public async Task Unknown_rooms_other_tenants_unknown_meetings_and_other_event_types_are_acknowledged_but_ignored()
    {
        var meeting = await Meeting();

        var foreignRoom = await Webhook(EventBody("participant_joined", "some-other-room", Member(_participant)));
        var unknownMeeting = await Webhook(EventBody("participant_joined", Room(Guid.NewGuid()), Member(_participant)));
        var unknownTenant = await Webhook(EventBody("participant_joined", MediaAccessService.RoomName(Guid.NewGuid(), meeting.Id), Member(_participant)));
        var badIdentity = await Webhook(EventBody("participant_joined", Room(meeting.Id), "intruder"));
        var otherType = await Webhook(EventBody("track_published", Room(meeting.Id), Member(_participant)));
        var wrongTenant = await Webhook(EventBody("participant_joined", Room(meeting.Id, _tenantB), Member(_participant)));

        Assert.All(new[] { foreignRoom, unknownMeeting, unknownTenant, badIdentity, otherType, wrongTenant }, r => Assert.Equal((HttpStatusCode.OK, "ignored"), r));
        Assert.Empty((await Presence(meeting.Id)).Stays);
    }

    [Fact]
    public async Task Without_media_settings_the_webhook_is_unavailable()
    {
        var client = Create(configured: false);
        var body = EventBody("participant_joined", Room(Guid.NewGuid()), Member(_participant));

        var result = await Webhook(body, client: client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Status);
    }

    [Fact]
    public async Task Presence_is_visible_only_to_report_readers_and_only_inside_the_tenant()
    {
        var meeting = await Meeting();
        await Webhook(EventBody("participant_joined", Room(meeting.Id), Member(_participant)));

        var participant = await Api(HttpMethod.Get, $"/api/v1/meetings/{meeting.Id}/presence", _tenant, _participant);
        var host = await Api(HttpMethod.Get, $"/api/v1/meetings/{meeting.Id}/presence", _tenant, _host);
        var otherTenant = await Api(HttpMethod.Get, $"/api/v1/meetings/{meeting.Id}/presence", _tenantB, _auditorB);
        var missing = await Api(HttpMethod.Get, $"/api/v1/meetings/{Guid.NewGuid()}/presence", _tenant, _auditor);

        Assert.Equal(HttpStatusCode.Forbidden, participant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, host.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherTenant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task The_attendance_report_shows_real_presence_next_to_the_invitation_counts()
    {
        var meeting = await Meeting();
        var room = Room(meeting.Id);
        var start = _t0;
        await Webhook(EventBody("participant_joined", room, Member(_participant), at: start));
        await Webhook(EventBody("participant_left", room, Member(_participant), at: start.AddMinutes(40)));
        await Webhook(EventBody("participant_joined", room, Member(_host), at: start.AddMinutes(5)));
        var from = Uri.EscapeDataString(start.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(start.AddDays(1).ToString("O"));

        var rows = (await (await Api(HttpMethod.Get, $"/api/v1/reports/attendance?from={from}&to={to}", _tenant, _auditor)).Content.ReadFromJsonAsync<List<AttendanceRow>>(Json))!;

        var row = Assert.Single(rows);
        Assert.Equal(2, row.Present);

        // The host never left: that stay is measured to the planned end of the meeting (one hour after the start).
        Assert.Equal(40 + 55, row.PresentMinutes);
    }
}
