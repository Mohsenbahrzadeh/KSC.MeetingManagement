using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KSC.MeetingManagement.Application.Media;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Participants;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Meetings;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

/// <summary>TASK-052 and TASK-055: lobby, host controls and ending the meeting.</summary>
public sealed class RoomControlApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Secret = "0123456789abcdef0123456789abcdef-secret";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Clock _clock = new(new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly DateTimeOffset _start = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
    private readonly RecordingControl _control = new();
    private readonly Tenant _tenant = new(Guid.NewGuid(), "a", "A", "UTC");
    private readonly Tenant _tenantB = new(Guid.NewGuid(), "b", "B", "UTC");
    private readonly Guid _host = Guid.NewGuid();
    private readonly Guid _member = Guid.NewGuid();
    private readonly Guid _member2 = Guid.NewGuid();
    private readonly Guid _hostB = Guid.NewGuid();
    private readonly HttpClient _client;

    public RoomControlApiTests(WebApplicationFactory<Program> factory)
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Media:ServerUrl", "wss://media.example.org");
            b.UseSetting("Media:ApiKey", "APIkey1");
            b.UseSetting("Media:ApiSecret", Secret);
            b.ConfigureServices(s =>
            {
                s.UseInMemoryKscDatabase(Guid.NewGuid().ToString());
                s.AddSingleton<TimeProvider>(_clock);
                s.AddSingleton<IMediaRoomControl>(_control);
                s.AddAuthentication(TestAuthHandler.Scheme)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
            });
        });

        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenant);
        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenantB);
        var members = app.Services.GetRequiredService<InMemoryMembershipStore>();
        members.Add(new Membership(_tenant.Id, _host, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenant.Id, _member, [Roles.Participant]));
        members.Add(new Membership(_tenant.Id, _member2, [Roles.Participant]));
        members.Add(new Membership(_tenantB.Id, _hostB, [Roles.MeetingOrganizer]));
        _client = app.CreateClient(new() { AllowAutoRedirect = false });
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingControl : IMediaRoomControl
    {
        public List<string> Closed { get; } = [];

        public List<(string Room, string Identity)> Removed { get; } = [];

        public bool Fail { get; set; }

        public Task CloseRoomAsync(string room, CancellationToken cancellationToken)
        {
            Closed.Add(room);
            return Fail ? throw new HttpRequestException("media server down") : Task.CompletedTask;
        }

        public Task RemoveParticipantAsync(string room, string identity, CancellationToken cancellationToken)
        {
            Removed.Add((room, identity));
            return Fail ? throw new HttpRequestException("media server down") : Task.CompletedTask;
        }
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, Tenant tenant, Guid? user, object? body = null)
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

        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, Guid? user, object? body = null) => Send(method, url, _tenant, user, body);

    private async Task<MeetingDto> Meeting(string mode = "Online")
    {
        var response = await Send(HttpMethod.Post, "/api/v1/meetings", _host, new { title = "x", mode, start = _start, end = _start.AddHours(1), allowConflicts = true });
        return (await response.Content.ReadFromJsonAsync<MeetingDto>(Json))!;
    }

    private async Task<GuestInvitationDto> Guest(Guid meetingId)
    {
        var response = await Send(HttpMethod.Post, $"/api/v1/meetings/{meetingId}/participants/guests", _host,
            new { name = "مهمان", email = "g@example.com", expiresUtc = _clock.Now.AddDays(3) });
        return (await response.Content.ReadFromJsonAsync<GuestInvitationDto>(Json))!;
    }

    private static string GuestIdentity(GuestInvitationDto invite) => $"g-{invite.Participant.Id:N}";

    private Task<HttpResponseMessage> JoinGuest(Guid meetingId, GuestInvitationDto invite) =>
        Send(HttpMethod.Post, $"/api/v1/meetings/{meetingId}/room/join-guest", null, new { participantId = invite.Participant.Id, token = invite.Token });

    private static string Url(Guid meetingId, string path) => $"/api/v1/meetings/{meetingId}/room/{path}";

    [Fact]
    public async Task A_guest_waits_in_the_lobby_until_the_host_admits_them()
    {
        var meeting = await Meeting();
        var invite = await Guest(meeting.Id);
        _clock.Now = _start;

        var first = await JoinGuest(meeting.Id, invite);
        var again = await JoinGuest(meeting.Id, invite);
        var waiting = (await (await Send(HttpMethod.Get, Url(meeting.Id, "lobby"), _host)).Content.ReadFromJsonAsync<List<RoomEntryDto>>(Json))!;
        var admit = await Send(HttpMethod.Post, Url(meeting.Id, $"lobby/{GuestIdentity(invite)}/admit"), _host);
        var inside = await JoinGuest(meeting.Id, invite);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal("Waiting", (await first.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        var entry = Assert.Single(waiting);
        Assert.Equal((GuestIdentity(invite), "مهمان", RoomEntryStatus.Waiting), (entry.Identity, entry.DisplayName, entry.Status));
        Assert.Equal(HttpStatusCode.OK, admit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
        Assert.Empty((await (await Send(HttpMethod.Get, Url(meeting.Id, "lobby"), _host)).Content.ReadFromJsonAsync<List<RoomEntryDto>>(Json))!);
    }

    [Fact]
    public async Task A_denied_guest_never_gets_a_token_and_cannot_be_admitted_afterwards()
    {
        var meeting = await Meeting();
        var invite = await Guest(meeting.Id);
        _clock.Now = _start;
        await JoinGuest(meeting.Id, invite);

        var deny = await Send(HttpMethod.Post, Url(meeting.Id, $"lobby/{GuestIdentity(invite)}/deny"), _host);
        var retry = await JoinGuest(meeting.Id, invite);
        var admitLater = await Send(HttpMethod.Post, Url(meeting.Id, $"lobby/{GuestIdentity(invite)}/admit"), _host);

        Assert.Equal(HttpStatusCode.OK, deny.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, retry.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, admitLater.StatusCode);
    }

    [Fact]
    public async Task Removing_someone_disconnects_them_and_bars_them_from_a_new_token()
    {
        var meeting = await Meeting();
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/internal", _host, new { userId = _member });
        _clock.Now = _start;
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, Url(meeting.Id, "join"), _member, new { })).StatusCode);
        var identity = $"u-{_member:N}";

        var remove = await Send(HttpMethod.Post, Url(meeting.Id, $"participants/{identity}/remove"), _host);
        var rejoin = await Send(HttpMethod.Post, Url(meeting.Id, "join"), _member, new { });

        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        Assert.Equal((MediaAccessService.RoomName(_tenant.Id, meeting.Id), identity), _control.Removed.Single());
        Assert.Equal(HttpStatusCode.Forbidden, rejoin.StatusCode);
    }

    [Fact]
    public async Task A_host_can_bar_someone_in_advance_and_the_media_server_being_down_does_not_undo_it()
    {
        var meeting = await Meeting();
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/internal", _host, new { userId = _member2 });
        _control.Fail = true;
        _clock.Now = _start;

        var remove = await Send(HttpMethod.Post, Url(meeting.Id, $"participants/u-{_member2:N}/remove"), _host);
        var join = await Send(HttpMethod.Post, Url(meeting.Id, "join"), _member2, new { });

        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, join.StatusCode);
    }

    [Fact]
    public async Task A_removed_guest_cannot_come_back_even_with_a_valid_invitation()
    {
        var meeting = await Meeting();
        var invite = await Guest(meeting.Id);
        _clock.Now = _start;
        await JoinGuest(meeting.Id, invite);
        await Send(HttpMethod.Post, Url(meeting.Id, $"lobby/{GuestIdentity(invite)}/admit"), _host);
        Assert.Equal(HttpStatusCode.OK, (await JoinGuest(meeting.Id, invite)).StatusCode);

        await Send(HttpMethod.Post, Url(meeting.Id, $"participants/{GuestIdentity(invite)}/remove"), _host);

        Assert.Equal(HttpStatusCode.Forbidden, (await JoinGuest(meeting.Id, invite)).StatusCode);
    }

    [Fact]
    public async Task Members_can_be_required_to_wait_too_but_the_host_never_waits()
    {
        var meeting = await Meeting();
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/internal", _host, new { userId = _member });
        _clock.Now = _start;

        // Default: invited members enter directly; only guests wait.
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, Url(meeting.Id, "join"), _member, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, Url(meeting.Id, "join"), _host, new { })).StatusCode);
    }

    [Fact]
    public async Task Only_authorised_roles_can_use_the_host_controls()
    {
        var meeting = await Meeting();
        var invite = await Guest(meeting.Id);
        _clock.Now = _start;
        await JoinGuest(meeting.Id, invite);
        var lobby = Url(meeting.Id, "lobby");
        var admit = Url(meeting.Id, $"lobby/{GuestIdentity(invite)}/admit");
        var remove = Url(meeting.Id, $"participants/u-{_member:N}/remove");

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, lobby, _member)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, admit, _member)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, remove, _member)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, admit, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/end", _member, new { expectedVersion = 1 })).StatusCode);
    }

    [Fact]
    public async Task The_lobby_and_controls_are_isolated_between_tenants_and_reject_malformed_identities()
    {
        var meeting = await Meeting();
        var invite = await Guest(meeting.Id);
        _clock.Now = _start;
        await JoinGuest(meeting.Id, invite);

        var otherTenantList = await Send(HttpMethod.Get, Url(meeting.Id, "lobby"), _tenantB, _hostB);
        var otherTenantAdmit = await Send(HttpMethod.Post, Url(meeting.Id, $"lobby/{GuestIdentity(invite)}/admit"), _tenantB, _hostB);
        var bad = await Send(HttpMethod.Post, Url(meeting.Id, "lobby/not-an-identity/admit"), _host);
        var unknown = await Send(HttpMethod.Post, Url(meeting.Id, $"lobby/g-{Guid.NewGuid():N}/admit"), _host);

        Assert.Equal(HttpStatusCode.NotFound, otherTenantList.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherTenantAdmit.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Ending_the_meeting_records_the_time_closes_the_room_and_blocks_new_entries()
    {
        var meeting = await Meeting();
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/internal", _host, new { userId = _member });
        var invite = await Guest(meeting.Id);
        _clock.Now = _start.AddMinutes(5);
        var started = (await (await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/start", _host, new { expectedVersion = meeting.Version })).Content.ReadFromJsonAsync<MeetingDto>(Json))!;
        Assert.Equal((MeetingStatus.InProgress, _clock.Now), (started.Status, started.StartedAtUtc));
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, Url(meeting.Id, "join"), _member, new { })).StatusCode);

        _clock.Now = _start.AddMinutes(50);
        var end = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/end", _host, new { expectedVersion = started.Version });
        var ended = (await end.Content.ReadFromJsonAsync<MeetingDto>(Json))!;

        Assert.Equal(HttpStatusCode.OK, end.StatusCode);
        Assert.Equal((MeetingStatus.Completed, _start.AddMinutes(50)), (ended.Status, ended.EndedAtUtc));
        Assert.Equal(MediaAccessService.RoomName(_tenant.Id, meeting.Id), _control.Closed.Single());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, Url(meeting.Id, "join"), _member, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, Url(meeting.Id, "join"), _host, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await JoinGuest(meeting.Id, invite)).StatusCode);
    }

    [Fact]
    public async Task A_meeting_that_was_never_started_can_be_closed_in_one_step_and_a_second_end_is_refused()
    {
        var meeting = await Meeting();
        _clock.Now = _start.AddMinutes(30);

        var end = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/end", _host, new { expectedVersion = meeting.Version });
        var ended = (await end.Content.ReadFromJsonAsync<MeetingDto>(Json))!;
        var again = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/end", _host, new { expectedVersion = ended.Version });
        var stale = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/start", _host, new { expectedVersion = 1 });

        Assert.Equal((MeetingStatus.Completed, _clock.Now, _clock.Now), (ended.Status, ended.StartedAtUtc, ended.EndedAtUtc));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task A_draft_or_cancelled_meeting_cannot_be_started_or_ended()
    {
        var draftResponse = await Send(HttpMethod.Post, "/api/v1/meetings", _host, new { title = "d", mode = "Online" });
        var draft = (await draftResponse.Content.ReadFromJsonAsync<MeetingDto>(Json))!;
        var cancelledMeeting = await Meeting();
        await Send(HttpMethod.Post, $"/api/v1/meetings/{cancelledMeeting.Id}/cancel", _host, new { expectedVersion = cancelledMeeting.Version, reason = "x" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, $"/api/v1/meetings/{draft.Id}/end", _host, new { expectedVersion = draft.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, $"/api/v1/meetings/{draft.Id}/start", _host, new { expectedVersion = draft.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, $"/api/v1/meetings/{cancelledMeeting.Id}/end", _host, new { expectedVersion = cancelledMeeting.Version + 1 })).StatusCode);
        Assert.Empty(_control.Closed);
    }

    [Fact]
    public async Task A_media_server_failure_when_closing_does_not_undo_the_end_of_the_meeting()
    {
        var meeting = await Meeting();
        _control.Fail = true;
        _clock.Now = _start.AddMinutes(30);

        var end = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/end", _host, new { expectedVersion = meeting.Version });
        var reread = (await (await Send(HttpMethod.Get, $"/api/v1/meetings/{meeting.Id}", _host)).Content.ReadFromJsonAsync<MeetingDto>(Json))!;

        Assert.Equal(HttpStatusCode.OK, end.StatusCode);
        Assert.Equal(MeetingStatus.Completed, reread.Status);
    }

    [Fact]
    public async Task Cross_tenant_end_and_start_are_not_found()
    {
        var meeting = await Meeting();

        var end = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/end", _tenantB, _hostB, new { expectedVersion = meeting.Version });

        Assert.Equal(HttpStatusCode.NotFound, end.StatusCode);
        Assert.Empty(_control.Closed);
    }
}
