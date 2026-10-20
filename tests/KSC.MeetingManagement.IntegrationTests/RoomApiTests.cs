using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Participants;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class RoomApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Secret = "0123456789abcdef0123456789abcdef-secret";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly DateTimeOffset _start = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
    private readonly WebApplicationFactory<Program> _baseFactory;
    private readonly Tenant _tenantA = new(Guid.NewGuid(), "a", "A", "UTC");
    private readonly Tenant _tenantB = new(Guid.NewGuid(), "b", "B", "UTC");
    private readonly Guid _organizer = Guid.NewGuid();
    private readonly Guid _invited = Guid.NewGuid();
    private readonly Guid _stranger = Guid.NewGuid();
    private readonly Guid _organizerB = Guid.NewGuid();
    private readonly HttpClient _client;

    public RoomApiTests(WebApplicationFactory<Program> factory)
    {
        _baseFactory = factory;
        _client = CreateClient(configured: true);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private HttpClient CreateClient(bool configured)
    {
        var app = _baseFactory.WithWebHostBuilder(b =>
        {
            if (configured)
            {
                b.UseSetting("Media:ServerUrl", "wss://media.example.org");
                b.UseSetting("Media:ApiKey", "APIkey1");
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

        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenantA);
        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenantB);
        var members = app.Services.GetRequiredService<InMemoryMembershipStore>();
        members.Add(new Membership(_tenantA.Id, _organizer, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenantA.Id, _invited, [Roles.Participant]));
        members.Add(new Membership(_tenantA.Id, _stranger, [Roles.Participant]));
        members.Add(new Membership(_tenantB.Id, _organizerB, [Roles.MeetingOrganizer]));
        return app.CreateClient(new() { AllowAutoRedirect = false });
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, Tenant tenant, Guid? user, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Tenant-Id", tenant.Id.ToString());
        if (user is { } u)
        {
            request.Headers.Add("X-Test-User", u.ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, Tenant tenant, Guid? user, object? body = null) =>
        Send(_client, method, url, tenant, user, body);

    private async Task<Guid> CreateMeeting(string mode = "Online", DateTimeOffset? start = null, HttpClient? client = null)
    {
        var s = start ?? _start;
        var response = await Send(client ?? _client, HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizer, new { title = "Board", mode, start = s, end = s.AddHours(1), allowConflicts = true });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MeetingDto>(Json))!.Id;
    }

    private async Task Invite(Guid meetingId) =>
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, $"/api/v1/meetings/{meetingId}/participants/internal", _tenantA, _organizer, new { userId = _invited })).StatusCode);

    private async Task<GuestInvitationDto> InviteGuest(Guid meetingId)
    {
        var response = await Send(HttpMethod.Post, $"/api/v1/meetings/{meetingId}/participants/guests", _tenantA, _organizer,
            new { name = "مهمان", email = "g@example.com", expiresUtc = _clock.Now.AddDays(2) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GuestInvitationDto>(Json))!;
    }

    private static JsonElement Claims(string token)
    {
        var part = token.Split('.')[1];
        return JsonDocument.Parse(Convert.FromBase64String(part.Replace('-', '+').Replace('_', '/').PadRight(part.Length + ((4 - (part.Length % 4)) % 4), '='))).RootElement;
    }

    private static string Url(Guid meetingId, string path) => $"/api/v1/meetings/{meetingId}/room/{path}";

    [Fact]
    public async Task The_organizer_joins_as_host_with_a_room_bound_to_tenant_and_meeting()
    {
        var id = await CreateMeeting();
        _clock.Now = _start;

        var response = await Send(HttpMethod.Post, Url(id, "join"), _tenantA, _organizer, new { displayName = "میزبان" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var claims = Claims(body.GetProperty("token").GetString()!);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Host", body.GetProperty("role").GetString());
        Assert.Equal("wss://media.example.org", body.GetProperty("serverUrl").GetString());
        Assert.Equal($"ksc-{_tenantA.Id:N}-{id:N}", claims.GetProperty("video").GetProperty("room").GetString());
        Assert.Equal($"u-{_organizer:N}", claims.GetProperty("sub").GetString());
        Assert.True(claims.GetProperty("video").GetProperty("roomAdmin").GetBoolean());
        Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invited_participant_joins_without_admin_rights_and_others_are_refused()
    {
        var id = await CreateMeeting();
        await Invite(id);
        _clock.Now = _start;

        var invited = await Send(HttpMethod.Post, Url(id, "join"), _tenantA, _invited, new { });
        var stranger = await Send(HttpMethod.Post, Url(id, "join"), _tenantA, _stranger, new { });
        var anonymous = await Send(HttpMethod.Post, Url(id, "join"), _tenantA, null, new { });

        var claims = Claims((await invited.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, invited.StatusCode);
        Assert.False(claims.GetProperty("video").GetProperty("roomAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task A_revoked_participant_cannot_join()
    {
        var id = await CreateMeeting();
        await Invite(id);
        _clock.Now = _start;
        var list = (await (await Send(HttpMethod.Get, $"/api/v1/meetings/{id}/participants", _tenantA, _organizer)).Content.ReadFromJsonAsync<List<ParticipantDto>>(Json))!;
        await Send(HttpMethod.Post, $"/api/v1/meetings/{id}/participants/{list.Single().Id}/revoke", _tenantA, _organizer);

        var response = await Send(HttpMethod.Post, Url(id, "join"), _tenantA, _invited, new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_room_is_open_only_in_the_join_window()
    {
        var id = await CreateMeeting();

        var tooEarly = await Send(HttpMethod.Post, Url(id, "join"), _tenantA, _organizer, new { });
        _clock.Now = _start.AddMinutes(-15);
        var opens = await Send(HttpMethod.Post, Url(id, "join"), _tenantA, _organizer, new { });
        _clock.Now = _start.AddHours(1).AddMinutes(120);
        var lastMoment = await Send(HttpMethod.Post, Url(id, "join"), _tenantA, _organizer, new { });
        _clock.Now = _start.AddHours(1).AddMinutes(121);
        var tooLate = await Send(HttpMethod.Post, Url(id, "join"), _tenantA, _organizer, new { });

        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);
        Assert.Equal(HttpStatusCode.OK, opens.StatusCode);
        Assert.Equal(HttpStatusCode.OK, lastMoment.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);
    }

    [Fact]
    public async Task In_person_cancelled_and_unscheduled_meetings_have_no_room()
    {
        var inPerson = await CreateMeeting("InPerson");
        var cancelledId = await CreateMeeting();
        var cancelled = (await (await Send(HttpMethod.Get, $"/api/v1/meetings/{cancelledId}", _tenantA, _organizer)).Content.ReadFromJsonAsync<MeetingDto>(Json))!;
        await Send(HttpMethod.Post, $"/api/v1/meetings/{cancelledId}/cancel", _tenantA, _organizer, new { expectedVersion = cancelled.Version, reason = "x" });
        var draft = (await (await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizer, new { title = "d", mode = "Online" })).Content.ReadFromJsonAsync<MeetingDto>(Json))!;
        _clock.Now = _start;

        var a = await Send(HttpMethod.Post, Url(inPerson, "join"), _tenantA, _organizer, new { });
        var b = await Send(HttpMethod.Post, Url(cancelledId, "join"), _tenantA, _organizer, new { });
        var c = await Send(HttpMethod.Post, Url(draft.Id, "join"), _tenantA, _organizer, new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, a.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, b.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, c.StatusCode);
    }

    [Fact]
    public async Task Other_tenants_cannot_join_and_unknown_meetings_are_not_found()
    {
        var id = await CreateMeeting();
        _clock.Now = _start;

        var otherTenant = await Send(HttpMethod.Post, Url(id, "join"), _tenantB, _organizerB, new { });
        var unknown = await Send(HttpMethod.Post, Url(Guid.NewGuid(), "join"), _tenantA, _organizer, new { });

        Assert.Equal(HttpStatusCode.NotFound, otherTenant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_guest_with_a_valid_invitation_joins_with_limited_rights()
    {
        var id = await CreateMeeting();
        var invite = await InviteGuest(id);
        _clock.Now = _start;

        var response = await Send(HttpMethod.Post, Url(id, "join-guest"), _tenantA, null, new { participantId = invite.Participant.Id, token = invite.Token });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var claims = Claims(body.GetProperty("token").GetString()!);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Guest", body.GetProperty("role").GetString());
        Assert.Equal($"g-{invite.Participant.Id:N}", claims.GetProperty("sub").GetString());
        Assert.Equal("مهمان", claims.GetProperty("name").GetString());
        Assert.False(claims.GetProperty("video").GetProperty("roomAdmin").GetBoolean());
        Assert.False(claims.GetProperty("video").GetProperty("canPublishData").GetBoolean());
    }

    [Fact]
    public async Task Bad_revoked_expired_or_foreign_guest_invitations_all_get_the_same_refusal()
    {
        var id = await CreateMeeting();
        var other = await CreateMeeting();
        var good = await InviteGuest(id);
        var revoked = await InviteGuest(id);
        await Send(HttpMethod.Post, $"/api/v1/meetings/{id}/participants/{revoked.Participant.Id}/revoke", _tenantA, _organizer);
        _clock.Now = _start;

        var wrongToken = await Send(HttpMethod.Post, Url(id, "join-guest"), _tenantA, null, new { participantId = good.Participant.Id, token = "nope" });
        var revokedTry = await Send(HttpMethod.Post, Url(id, "join-guest"), _tenantA, null, new { participantId = revoked.Participant.Id, token = revoked.Token });
        var wrongMeeting = await Send(HttpMethod.Post, Url(other, "join-guest"), _tenantA, null, new { participantId = good.Participant.Id, token = good.Token });
        var otherTenant = await Send(HttpMethod.Post, Url(id, "join-guest"), _tenantB, null, new { participantId = good.Participant.Id, token = good.Token });
        var unknownMeeting = await Send(HttpMethod.Post, Url(Guid.NewGuid(), "join-guest"), _tenantA, null, new { participantId = good.Participant.Id, token = good.Token });
        _clock.Now = _start.AddDays(3);
        var expired = await Send(HttpMethod.Post, Url(id, "join-guest"), _tenantA, null, new { participantId = good.Participant.Id, token = good.Token });

        Assert.All(
            new[] { wrongToken, revokedTry, wrongMeeting, otherTenant, unknownMeeting },
            r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.NotEqual(HttpStatusCode.OK, expired.StatusCode);
    }

    [Fact]
    public async Task An_early_guest_visit_does_not_use_up_the_invitation()
    {
        var id = await CreateMeeting();
        var invite = await InviteGuest(id);

        var early = await Send(HttpMethod.Post, Url(id, "join-guest"), _tenantA, null, new { participantId = invite.Participant.Id, token = invite.Token });
        _clock.Now = _start;
        var onTime = await Send(HttpMethod.Post, Url(id, "join-guest"), _tenantA, null, new { participantId = invite.Participant.Id, token = invite.Token });

        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal(HttpStatusCode.OK, onTime.StatusCode);
    }

    [Fact]
    public async Task Without_media_settings_joining_is_unavailable_instead_of_failing_open()
    {
        var client = CreateClient(configured: false);
        var id = await CreateMeeting(client: client);
        _clock.Now = _start;

        var response = await Send(client, HttpMethod.Post, Url(id, "join"), _tenantA, _organizer, new { });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("token", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
