using System.Net;
using System.Net.Http.Json;
using KSC.MeetingManagement.Application.Actions;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Resolutions;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Meetings;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class ActionsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly Tenant _tenantA = new(Guid.NewGuid(), "a", "A", "UTC");
    private readonly Tenant _tenantB = new(Guid.NewGuid(), "b", "B", "UTC");
    private readonly Guid _organizer = Guid.NewGuid();
    private readonly Guid _secretary = Guid.NewGuid();
    private readonly Guid _auditor = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _otherMember = Guid.NewGuid();
    private readonly Guid _outsider = Guid.NewGuid();
    private readonly Guid _organizerB = Guid.NewGuid();
    private readonly Guid _auditorB = Guid.NewGuid();
    private readonly HttpClient _client;

    public ActionsApiTests(WebApplicationFactory<Program> factory)
    {
        var app = factory.WithWebHostBuilder(b =>
        {
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
        members.Add(new Membership(_tenantA.Id, _secretary, [Roles.Secretary]));
        members.Add(new Membership(_tenantA.Id, _auditor, [Roles.Auditor]));
        members.Add(new Membership(_tenantA.Id, _owner, [Roles.Participant]));
        members.Add(new Membership(_tenantA.Id, _otherMember, [Roles.Participant]));
        members.Add(new Membership(_tenantB.Id, _organizerB, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenantB.Id, _auditorB, [Roles.Auditor]));

        _client = app.CreateClient(new() { AllowAutoRedirect = false });
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, Tenant tenant, Guid user, object? body = null)
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

    private async Task<Guid> CreateMeeting()
    {
        var response = await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizer, new { title = "Board", mode = "Online" });
        return (await response.Content.ReadFromJsonAsync<MeetingDto>(Json))!.Id;
    }

    private static string Url(Guid meetingId, string suffix = "") => $"/api/v1/meetings/{meetingId}/actions{suffix}";

    private static async Task<ActionItemDto> Read(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ActionItemDto>(Json))!;
    }

    private async Task<ActionItemDto> Create(Guid meetingId, DateOnly due, Guid? owner = null, string title = "اقدام", Guid? resolutionId = null) =>
        await Read(await Send(HttpMethod.Post, Url(meetingId), _tenantA, _secretary,
            new { title, ownerUserId = owner ?? _owner, dueDate = due, resolutionId }));

    private DateOnly Today => DateOnly.FromDateTime(_clock.Now.UtcDateTime);

    [Fact]
    public async Task Manager_creates_an_action_with_owner_and_due_day_and_the_meeting_lists_it()
    {
        var meetingId = await CreateMeeting();

        var created = await Create(meetingId, Today.AddDays(7));
        var list = (await (await Send(HttpMethod.Get, Url(meetingId), _tenantA, _otherMember)).Content.ReadFromJsonAsync<List<ActionItemDto>>(Json))!;

        Assert.Equal((_owner, Today.AddDays(7), ActionItemStatus.Open, false), (created.OwnerUserId, created.DueDate, created.Status, created.IsOverdue));
        Assert.Contains(list, a => a.Id == created.Id);
    }

    [Fact]
    public async Task Invalid_input_is_rejected()
    {
        var meetingId = await CreateMeeting();
        var otherMeeting = await CreateMeeting();
        var resolution = (await (await Send(HttpMethod.Post, $"/api/v1/meetings/{otherMeeting}/resolutions", _tenantA, _secretary, new { text = "x" }))
            .Content.ReadFromJsonAsync<ResolutionDto>(Json))!;

        var past = await Send(HttpMethod.Post, Url(meetingId), _tenantA, _secretary, new { title = "x", ownerUserId = _owner, dueDate = Today.AddDays(-1) });
        var stranger = await Send(HttpMethod.Post, Url(meetingId), _tenantA, _secretary, new { title = "x", ownerUserId = _outsider, dueDate = Today.AddDays(1) });
        var blank = await Send(HttpMethod.Post, Url(meetingId), _tenantA, _secretary, new { title = " ", ownerUserId = _owner, dueDate = Today.AddDays(1) });
        var foreignResolution = await Send(HttpMethod.Post, Url(meetingId), _tenantA, _secretary,
            new { title = "x", ownerUserId = _owner, dueDate = Today.AddDays(1), resolutionId = resolution.Id });
        var missingMeeting = await Send(HttpMethod.Post, Url(Guid.NewGuid()), _tenantA, _secretary, new { title = "x", ownerUserId = _owner, dueDate = Today.AddDays(1) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, past.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stranger.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreignResolution.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingMeeting.StatusCode);
    }

    [Fact]
    public async Task An_action_can_be_linked_to_a_resolution_of_the_same_meeting()
    {
        var meetingId = await CreateMeeting();
        var resolution = (await (await Send(HttpMethod.Post, $"/api/v1/meetings/{meetingId}/resolutions", _tenantA, _secretary, new { text = "x" }))
            .Content.ReadFromJsonAsync<ResolutionDto>(Json))!;

        var action = await Create(meetingId, Today.AddDays(3), resolutionId: resolution.Id);

        Assert.Equal(resolution.Id, action.ResolutionId);
    }

    [Fact]
    public async Task The_owner_completes_their_own_action_and_the_completion_is_recorded()
    {
        var meetingId = await CreateMeeting();
        var action = await Create(meetingId, Today.AddDays(3));
        var url = Url(meetingId, $"/{action.Id}");

        var started = await Read(await Send(HttpMethod.Post, $"{url}/start", _tenantA, _owner, new { expectedLockVersion = action.LockVersion }));
        _clock.Now = _clock.Now.AddHours(5);
        var done = await Read(await Send(HttpMethod.Post, $"{url}/complete", _tenantA, _owner, new { expectedLockVersion = started.LockVersion, note = "انجام شد" }));
        var again = await Send(HttpMethod.Post, $"{url}/complete", _tenantA, _owner, new { expectedLockVersion = done.LockVersion });

        Assert.Equal(ActionItemStatus.Done, done.Status);
        Assert.Equal(_owner, done.CompletedByUserId);
        Assert.Equal(_clock.Now, done.CompletedAtUtc);
        Assert.Equal("انجام شد", done.Note);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
    }

    [Fact]
    public async Task Others_cannot_complete_someone_elses_action_but_a_manager_can()
    {
        var meetingId = await CreateMeeting();
        var action = await Create(meetingId, Today.AddDays(3));
        var url = Url(meetingId, $"/{action.Id}/complete");

        var other = await Send(HttpMethod.Post, url, _tenantA, _otherMember, new { expectedLockVersion = action.LockVersion });
        var manager = await Send(HttpMethod.Post, url, _tenantA, _secretary, new { expectedLockVersion = action.LockVersion });

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(HttpStatusCode.OK, manager.StatusCode);
    }

    [Fact]
    public async Task Stale_versions_conflict_and_cancel_needs_a_reason()
    {
        var meetingId = await CreateMeeting();
        var action = await Create(meetingId, Today.AddDays(3));
        var url = Url(meetingId, $"/{action.Id}");
        await Send(HttpMethod.Post, $"{url}/start", _tenantA, _owner, new { expectedLockVersion = action.LockVersion });

        var stale = await Send(HttpMethod.Post, $"{url}/complete", _tenantA, _owner, new { expectedLockVersion = action.LockVersion });
        var noReason = await Send(HttpMethod.Post, $"{url}/cancel", _tenantA, _secretary, new { expectedLockVersion = action.LockVersion + 1, reason = " " });
        var cancelled = await Read(await Send(HttpMethod.Post, $"{url}/cancel", _tenantA, _secretary, new { expectedLockVersion = action.LockVersion + 1, reason = "لغو" }));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        Assert.Equal(ActionItemStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task The_overdue_report_lists_only_active_actions_past_their_due_day_oldest_first()
    {
        var meetingId = await CreateMeeting();
        var early = await Create(meetingId, Today.AddDays(2), title: "زودتر");
        var late = await Create(meetingId, Today.AddDays(4), title: "دیرتر");
        var today = await Create(meetingId, Today.AddDays(5), title: "سررسید امروز");
        var done = await Create(meetingId, Today.AddDays(1), title: "انجام‌شده");
        var future = await Create(meetingId, Today.AddDays(30), title: "آینده");
        await Send(HttpMethod.Post, Url(meetingId, $"/{done.Id}/complete"), _tenantA, _owner, new { expectedLockVersion = done.LockVersion });

        Assert.Empty((await (await Send(HttpMethod.Get, "/api/v1/actions/overdue", _tenantA, _auditor)).Content.ReadFromJsonAsync<List<ActionItemDto>>(Json))!);

        _clock.Now = _clock.Now.AddDays(6);
        var report = (await (await Send(HttpMethod.Get, "/api/v1/actions/overdue", _tenantA, _auditor)).Content.ReadFromJsonAsync<List<ActionItemDto>>(Json))!;

        Assert.Equal([early.Id, late.Id, today.Id], report.Select(a => a.Id));
        Assert.All(report, a => Assert.True(a.IsOverdue));
        Assert.DoesNotContain(report, a => a.Id == done.Id || a.Id == future.Id);
    }

    [Fact]
    public async Task A_task_due_today_is_not_overdue_until_the_day_ends()
    {
        var meetingId = await CreateMeeting();
        var action = await Create(meetingId, Today);

        var sameDay = (await (await Send(HttpMethod.Get, "/api/v1/actions/overdue", _tenantA, _auditor)).Content.ReadFromJsonAsync<List<ActionItemDto>>(Json))!;
        _clock.Now = _clock.Now.AddDays(1);
        var nextDay = (await (await Send(HttpMethod.Get, "/api/v1/actions/overdue", _tenantA, _auditor)).Content.ReadFromJsonAsync<List<ActionItemDto>>(Json))!;

        Assert.Empty(sameDay);
        Assert.Equal(action.Id, nextDay.Single().Id);
    }

    [Fact]
    public async Task The_overdue_report_can_be_filtered_by_owner_and_needs_report_access()
    {
        var meetingId = await CreateMeeting();
        await Create(meetingId, Today.AddDays(1), owner: _owner);
        await Create(meetingId, Today.AddDays(1), owner: _otherMember);
        _clock.Now = _clock.Now.AddDays(3);

        var mine = (await (await Send(HttpMethod.Get, $"/api/v1/actions/overdue?ownerUserId={_owner}", _tenantA, _auditor)).Content.ReadFromJsonAsync<List<ActionItemDto>>(Json))!;
        var participant = await Send(HttpMethod.Get, "/api/v1/actions/overdue", _tenantA, _owner);
        var otherTenant = (await (await Send(HttpMethod.Get, "/api/v1/actions/overdue", _tenantB, _auditorB)).Content.ReadFromJsonAsync<List<ActionItemDto>>(Json))!;

        Assert.Equal(_owner, mine.Single().OwnerUserId);
        Assert.Equal(HttpStatusCode.Forbidden, participant.StatusCode);
        Assert.Empty(otherTenant);
    }

    [Fact]
    public async Task Mine_returns_only_the_callers_active_actions()
    {
        var meetingId = await CreateMeeting();
        var mineAction = await Create(meetingId, Today.AddDays(2), owner: _owner);
        await Create(meetingId, Today.AddDays(2), owner: _otherMember);
        var finished = await Create(meetingId, Today.AddDays(2), owner: _owner, title: "تمام");
        await Send(HttpMethod.Post, Url(meetingId, $"/{finished.Id}/complete"), _tenantA, _owner, new { expectedLockVersion = finished.LockVersion });

        var active = (await (await Send(HttpMethod.Get, "/api/v1/actions/mine", _tenantA, _owner)).Content.ReadFromJsonAsync<List<ActionItemDto>>(Json))!;
        var all = (await (await Send(HttpMethod.Get, "/api/v1/actions/mine?activeOnly=false", _tenantA, _owner)).Content.ReadFromJsonAsync<List<ActionItemDto>>(Json))!;

        Assert.Equal(mineAction.Id, active.Single().Id);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Creating_needs_the_manage_permission_and_tenants_are_isolated()
    {
        var meetingId = await CreateMeeting();
        var action = await Create(meetingId, Today.AddDays(2));
        var body = new { title = "x", ownerUserId = _owner, dueDate = Today.AddDays(2) };

        var participant = await Send(HttpMethod.Post, Url(meetingId), _tenantA, _otherMember, body);
        var auditor = await Send(HttpMethod.Post, Url(meetingId), _tenantA, _auditor, body);
        var crossRead = await Send(HttpMethod.Get, Url(meetingId), _tenantB, _organizerB);
        var crossComplete = await Send(HttpMethod.Post, Url(meetingId, $"/{action.Id}/complete"), _tenantB, _organizerB, new { expectedLockVersion = action.LockVersion });

        Assert.Equal(HttpStatusCode.Forbidden, participant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, auditor.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossComplete.StatusCode);
    }

    [Fact]
    public async Task A_manager_can_reassign_an_open_action()
    {
        var meetingId = await CreateMeeting();
        var action = await Create(meetingId, Today.AddDays(2));

        var updated = await Read(await Send(HttpMethod.Put, Url(meetingId, $"/{action.Id}"), _tenantA, _secretary,
            new { expectedLockVersion = action.LockVersion, title = "جدید", ownerUserId = _otherMember, dueDate = Today.AddDays(9) }));
        var badOwner = await Send(HttpMethod.Put, Url(meetingId, $"/{action.Id}"), _tenantA, _secretary,
            new { expectedLockVersion = updated.LockVersion, title = "x", ownerUserId = _outsider, dueDate = Today.AddDays(9) });

        Assert.Equal((_otherMember, Today.AddDays(9), "جدید"), (updated.OwnerUserId, updated.DueDate, updated.Title));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badOwner.StatusCode);
    }
}
