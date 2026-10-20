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

/// <summary>TASK-064: every expired, revoked or mismatched credential must be refused, at the exact boundaries.</summary>
public sealed class ExpiryAndRevocationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Secret = "0123456789abcdef0123456789abcdef-secret";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Clock _clock = new(new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly DateTimeOffset _start = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
    private readonly Tenant _tenant = new(Guid.NewGuid(), "a", "A", "UTC");
    private readonly Guid _organizer = Guid.NewGuid();
    private readonly Guid _member = Guid.NewGuid();
    private readonly HttpClient _client;

    public ExpiryAndRevocationTests(WebApplicationFactory<Program> factory)
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Media:ServerUrl", "wss://media.example.org");
            b.UseSetting("Media:ApiKey", "APIkey1");
            b.UseSetting("Media:ApiSecret", Secret);
            // These tests are about tokens and windows; the lobby has its own tests.
            b.UseSetting("Media:LobbyForGuests", "false");
            b.ConfigureServices(s =>
            {
                s.UseInMemoryKscDatabase(Guid.NewGuid().ToString());
                s.AddSingleton<TimeProvider>(_clock);
                s.AddAuthentication(TestAuthHandler.Scheme)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
            });
        });

        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenant);
        var members = app.Services.GetRequiredService<InMemoryMembershipStore>();
        members.Add(new Membership(_tenant.Id, _organizer, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenant.Id, _member, [Roles.Participant]));
        _client = app.CreateClient(new() { AllowAutoRedirect = false });
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, Guid? user, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Tenant-Id", _tenant.Id.ToString());
        if (user is { } u)
        {
            request.Headers.Add("X-Test-User", u.ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _client.SendAsync(request);
    }

    private async Task<MeetingDto> Meeting(DateTimeOffset? start = null)
    {
        var s = start ?? _start;
        var response = await Send(HttpMethod.Post, "/api/v1/meetings", _organizer, new { title = "x", mode = "Online", start = s, end = s.AddHours(1), allowConflicts = true });
        return (await response.Content.ReadFromJsonAsync<MeetingDto>(Json))!;
    }

    private async Task<GuestInvitationDto> Invite(Guid meetingId, TimeSpan? lifetime = null)
    {
        var response = await Send(HttpMethod.Post, $"/api/v1/meetings/{meetingId}/participants/guests", _organizer,
            new { name = "مهمان", email = "g@example.com", expiresUtc = _clock.Now + (lifetime ?? TimeSpan.FromHours(3)) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GuestInvitationDto>(Json))!;
    }

    private Task<HttpResponseMessage> Redeem(Guid meetingId, GuestInvitationDto invite, string? token = null) =>
        Send(HttpMethod.Post, $"/api/v1/meetings/{meetingId}/participants/{invite.Participant.Id}/redeem", null, new { token = token ?? invite.Token });

    private Task<HttpResponseMessage> JoinGuest(Guid meetingId, GuestInvitationDto invite, string? token = null) =>
        Send(HttpMethod.Post, $"/api/v1/meetings/{meetingId}/room/join-guest", null, new { participantId = invite.Participant.Id, token = token ?? invite.Token });

    [Fact]
    public async Task An_invitation_is_valid_until_the_last_instant_before_expiry()
    {
        var m = await Meeting();
        var invite = await Invite(m.Id, TimeSpan.FromHours(2));
        var expiry = invite.Participant.InviteExpiresUtc!.Value;

        _clock.Now = expiry.AddTicks(-1);
        var before = await Redeem(m.Id, invite);
        _clock.Now = expiry;
        var atExpiry = await Redeem(m.Id, invite);
        _clock.Now = expiry.AddSeconds(1);
        var after = await Redeem(m.Id, invite);

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, atExpiry.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task An_expired_invitation_cannot_enter_the_room_even_inside_the_meeting_window()
    {
        var m = await Meeting();
        var invite = await Invite(m.Id, TimeSpan.FromMinutes(30));
        _clock.Now = _start.AddMinutes(10);

        var response = await JoinGuest(m.Id, invite);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_revoked_invitation_stops_working_even_after_it_was_used()
    {
        var m = await Meeting();
        var invite = await Invite(m.Id);
        _clock.Now = _start;
        Assert.Equal(HttpStatusCode.OK, (await JoinGuest(m.Id, invite)).StatusCode);

        await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/participants/{invite.Participant.Id}/revoke", _organizer);

        Assert.Equal(HttpStatusCode.Forbidden, (await JoinGuest(m.Id, invite)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Redeem(m.Id, invite)).StatusCode);
    }

    [Fact]
    public async Task An_invitation_works_only_for_its_own_participant_and_meeting()
    {
        var first = await Meeting();
        var second = await Meeting(_start.AddDays(1));
        var inviteA = await Invite(first.Id);
        var inviteB = await Invite(first.Id);
        _clock.Now = _start;

        var swappedParticipant = await Send(HttpMethod.Post, $"/api/v1/meetings/{first.Id}/room/join-guest", null,
            new { participantId = inviteA.Participant.Id, token = inviteB.Token });
        var otherMeeting = await Send(HttpMethod.Post, $"/api/v1/meetings/{second.Id}/room/join-guest", null,
            new { participantId = inviteA.Participant.Id, token = inviteA.Token });

        Assert.Equal(HttpStatusCode.Forbidden, swappedParticipant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherMeeting.StatusCode);
    }

    [Fact]
    public async Task A_guest_invitation_cannot_be_used_as_an_internal_participant_and_the_reverse()
    {
        var m = await Meeting();
        var invite = await Invite(m.Id);
        var internalParticipant = (await (await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/participants/internal", _organizer, new { userId = _member })).Content
            .ReadFromJsonAsync<ParticipantDto>(Json))!;
        _clock.Now = _start;

        var internalAsGuest = await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/room/join-guest", null,
            new { participantId = internalParticipant.Id, token = invite.Token });
        var guestAsInternal = await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/room/join", Guid.NewGuid(), new { });

        Assert.Equal(HttpStatusCode.Forbidden, internalAsGuest.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, guestAsInternal.StatusCode);
    }

    [Fact]
    public async Task Cancelling_or_moving_the_meeting_closes_the_door_for_guests_and_members()
    {
        var m = await Meeting();
        var invite = await Invite(m.Id);
        await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/participants/internal", _organizer, new { userId = _member });
        _clock.Now = _start;
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/room/join", _member, new { })).StatusCode);

        var moved = await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/reschedule", _organizer,
            new { expectedVersion = m.Version, start = _start.AddDays(3), end = _start.AddDays(3).AddHours(1), allowConflicts = true });
        var movedBody = (await moved.Content.ReadFromJsonAsync<MeetingDto>(Json))!;

        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/room/join", _member, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await JoinGuest(m.Id, invite)).StatusCode);

        await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/cancel", _organizer, new { expectedVersion = movedBody.Version, reason = "x" });
        _clock.Now = _start.AddDays(3);

        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/room/join", _member, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await JoinGuest(m.Id, invite)).StatusCode);
    }

    [Fact]
    public async Task A_removed_member_cannot_get_a_new_room_token()
    {
        var m = await Meeting();
        var added = (await (await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/participants/internal", _organizer, new { userId = _member })).Content
            .ReadFromJsonAsync<ParticipantDto>(Json))!;
        _clock.Now = _start;
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/room/join", _member, new { })).StatusCode);

        await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/participants/{added.Id}/revoke", _organizer);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/room/join", _member, new { })).StatusCode);
    }

    [Fact]
    public async Task Issued_room_tokens_are_short_lived_and_bound_to_one_room()
    {
        var first = await Meeting();
        var second = await Meeting(_start.AddDays(1));
        _clock.Now = _start;

        var tokenA = await TokenFor(first.Id);
        var tokenB = await TokenFor(second.Id, _start.AddDays(1));

        Assert.True(tokenA.Exp - tokenA.Nbf <= TimeSpan.FromMinutes(10).TotalSeconds);
        Assert.Equal(_start.ToUnixTimeSeconds(), tokenA.Nbf);
        Assert.Equal($"ksc-{_tenant.Id:N}-{first.Id:N}", tokenA.Room);
        Assert.NotEqual(tokenA.Room, tokenB.Room);
        Assert.NotEqual(tokenA.Sub, string.Empty);
    }

    private async Task<(long Nbf, long Exp, string Room, string Sub)> TokenFor(Guid meetingId, DateTimeOffset? at = null)
    {
        if (at is { } when)
        {
            _clock.Now = when;
        }

        var response = await Send(HttpMethod.Post, $"/api/v1/meetings/{meetingId}/room/join", _organizer, new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
        var part = token.Split('.')[1];
        var claims = JsonDocument.Parse(Convert.FromBase64String(part.Replace('-', '+').Replace('_', '/').PadRight(part.Length + ((4 - (part.Length % 4)) % 4), '='))).RootElement;
        return (claims.GetProperty("nbf").GetInt64(), claims.GetProperty("exp").GetInt64(), claims.GetProperty("video").GetProperty("room").GetString()!, claims.GetProperty("sub").GetString()!);
    }
}
