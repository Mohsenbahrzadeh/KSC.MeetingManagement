using System.Net;
using System.Net.Http.Json;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Minutes;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Meetings;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class MinutesApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Tenant _tenantA = new(Guid.NewGuid(), "a", "A", "Asia/Tehran");
    private readonly Tenant _tenantB = new(Guid.NewGuid(), "b", "B", "Asia/Tehran");
    private readonly Guid _organizer = Guid.NewGuid();
    private readonly Guid _secretary = Guid.NewGuid();
    private readonly Guid _approver = Guid.NewGuid();
    private readonly Guid _participant = Guid.NewGuid();
    private readonly Guid _secretaryB = Guid.NewGuid();
    private readonly HttpClient _client;

    public MinutesApiTests(WebApplicationFactory<Program> factory)
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(s =>
            {
                s.UseInMemoryKscDatabase(Guid.NewGuid().ToString());
                s.AddAuthentication(TestAuthHandler.Scheme)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
            });
        });

        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenantA);
        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenantB);
        var members = app.Services.GetRequiredService<InMemoryMembershipStore>();
        members.Add(new Membership(_tenantA.Id, _organizer, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenantA.Id, _secretary, [Roles.Secretary]));
        members.Add(new Membership(_tenantA.Id, _approver, [Roles.Approver]));
        members.Add(new Membership(_tenantA.Id, _participant, [Roles.Participant]));
        members.Add(new Membership(_tenantB.Id, _secretaryB, [Roles.Secretary]));

        _client = app.CreateClient(new() { AllowAutoRedirect = false });
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

    private static string Url(Guid id, string suffix = "") => $"/api/v1/meetings/{id}/minutes{suffix}";

    private static async Task<MinutesDto> Read(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MinutesDto>(Json))!;
    }

    private async Task<MinutesDto> Draft(Guid id, string content = "متن") =>
        await Read(await Send(HttpMethod.Put, Url(id), _tenantA, _secretary, new { content, expectedVersionNumber = 0 }));

    private async Task<MinutesDto> Approve(Guid id)
    {
        await Send(HttpMethod.Post, Url(id, "/submit"), _tenantA, _secretary);
        return await Read(await Send(HttpMethod.Post, Url(id, "/approve"), _tenantA, _approver, new { comment = "ok" }));
    }

    [Fact]
    public async Task Secretary_drafts_submits_and_approver_approves_with_recorded_actors()
    {
        var id = await CreateMeeting();
        await Draft(id);

        var submitted = await Read(await Send(HttpMethod.Post, Url(id, "/submit"), _tenantA, _secretary));
        var approved = await Read(await Send(HttpMethod.Post, Url(id, "/approve"), _tenantA, _approver, new { comment = "تأیید" }));

        Assert.Equal(MinutesStatus.InReview, submitted.Status);
        Assert.Equal(MinutesStatus.Approved, approved.Status);
        Assert.Equal(1, approved.ApprovedVersionNumber);
        Assert.Equal([_secretary, _approver], approved.Transitions.Select(t => t.ActorUserId));
    }

    [Fact]
    public async Task Saving_creates_versions_and_a_stale_version_number_is_a_conflict()
    {
        var id = await CreateMeeting();
        await Draft(id, "v1");
        var v2 = await Read(await Send(HttpMethod.Put, Url(id), _tenantA, _secretary, new { content = "v2", expectedVersionNumber = 1 }));

        var stale = await Send(HttpMethod.Put, Url(id), _tenantA, _secretary, new { content = "v3", expectedVersionNumber = 1 });
        var versions = (await (await Send(HttpMethod.Get, Url(id, "/versions"), _tenantA, _secretary)).Content.ReadFromJsonAsync<List<MinutesVersionSummaryDto>>(Json))!;
        var first = (await (await Send(HttpMethod.Get, Url(id, "/versions/1"), _tenantA, _secretary)).Content.ReadFromJsonAsync<MinutesVersionDto>(Json))!;

        Assert.Equal(2, v2.CurrentVersionNumber);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal([1, 2], versions.Select(v => v.Number));
        Assert.Equal("v1", first.Content);
    }

    [Fact]
    public async Task Approved_minutes_cannot_be_edited_but_can_be_reopened_by_an_approver()
    {
        var id = await CreateMeeting();
        await Draft(id, "نهایی");
        await Approve(id);

        var edit = await Send(HttpMethod.Put, Url(id), _tenantA, _secretary, new { content = "دست‌کاری", expectedVersionNumber = 1 });
        var secretaryReopen = await Send(HttpMethod.Post, Url(id, "/reopen"), _tenantA, _secretary, new { comment = "x" });
        var noReason = await Send(HttpMethod.Post, Url(id, "/reopen"), _tenantA, _approver, new { comment = " " });
        var reopened = await Read(await Send(HttpMethod.Post, Url(id, "/reopen"), _tenantA, _approver, new { comment = "اصلاح لازم" }));
        var again = await Read(await Send(HttpMethod.Put, Url(id), _tenantA, _secretary, new { content = "اصلاح‌شده", expectedVersionNumber = 1 }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, secretaryReopen.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        Assert.Equal(MinutesStatus.Draft, reopened.Status);
        Assert.Equal(2, again.CurrentVersionNumber);
        var original = (await (await Send(HttpMethod.Get, Url(id, "/versions/1"), _tenantA, _secretary)).Content.ReadFromJsonAsync<MinutesVersionDto>(Json))!;
        Assert.Equal("نهایی", original.Content);
    }

    [Fact]
    public async Task Rejection_needs_a_comment_and_is_recorded()
    {
        var id = await CreateMeeting();
        await Draft(id);
        await Send(HttpMethod.Post, Url(id, "/submit"), _tenantA, _secretary);

        var blank = await Send(HttpMethod.Post, Url(id, "/reject"), _tenantA, _approver, new { comment = "" });
        var rejected = await Read(await Send(HttpMethod.Post, Url(id, "/reject"), _tenantA, _approver, new { comment = "نیاز به اصلاح" }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        Assert.Equal(MinutesStatus.Rejected, rejected.Status);
        Assert.Equal("نیاز به اصلاح", rejected.Transitions[^1].Comment);
        Assert.Equal(_approver, rejected.Transitions[^1].ActorUserId);
    }

    [Fact]
    public async Task Roles_are_enforced_for_writing_and_approving()
    {
        var id = await CreateMeeting();
        await Draft(id);

        var organizerWrite = await Send(HttpMethod.Put, Url(id), _tenantA, _organizer, new { content = "x", expectedVersionNumber = 1 });
        var participantSubmit = await Send(HttpMethod.Post, Url(id, "/submit"), _tenantA, _participant);
        await Send(HttpMethod.Post, Url(id, "/submit"), _tenantA, _secretary);
        var secretaryApprove = await Send(HttpMethod.Post, Url(id, "/approve"), _tenantA, _secretary, new { comment = "x" });
        var approverWrite = await Send(HttpMethod.Put, Url(id), _tenantA, _approver, new { content = "x", expectedVersionNumber = 1 });

        Assert.Equal(HttpStatusCode.Forbidden, organizerWrite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, participantSubmit.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, secretaryApprove.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, approverWrite.StatusCode);
    }

    [Fact]
    public async Task Unapproved_text_is_hidden_from_ordinary_participants_until_approval()
    {
        var id = await CreateMeeting();
        await Draft(id, "پیش‌نویس محرمانه");

        var hidden = await Send(HttpMethod.Get, Url(id), _tenantA, _participant);
        var hiddenVersions = await Send(HttpMethod.Get, Url(id, "/versions/1"), _tenantA, _participant);
        var seenByWriter = await Send(HttpMethod.Get, Url(id), _tenantA, _secretary);
        var seenByApprover = await Send(HttpMethod.Get, Url(id), _tenantA, _approver);
        await Approve(id);
        var visible = await Read(await Send(HttpMethod.Get, Url(id), _tenantA, _participant));

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hiddenVersions.StatusCode);
        Assert.Equal(HttpStatusCode.OK, seenByWriter.StatusCode);
        Assert.Equal(HttpStatusCode.OK, seenByApprover.StatusCode);
        Assert.Equal("پیش‌نویس محرمانه", visible.Content);
    }

    [Fact]
    public async Task Invalid_actions_and_missing_meetings_are_handled()
    {
        var id = await CreateMeeting();

        var approveMissing = await Send(HttpMethod.Post, Url(id, "/approve"), _tenantA, _approver, new { comment = "x" });
        var blank = await Send(HttpMethod.Put, Url(id), _tenantA, _secretary, new { content = " ", expectedVersionNumber = 0 });
        var missingMeeting = await Send(HttpMethod.Put, Url(Guid.NewGuid()), _tenantA, _secretary, new { content = "x", expectedVersionNumber = 0 });
        await Draft(id);
        var approveDraft = await Send(HttpMethod.Post, Url(id, "/approve"), _tenantA, _approver, new { comment = "x" });
        var createAgain = await Send(HttpMethod.Put, Url(id), _tenantA, _secretary, new { content = "y", expectedVersionNumber = 0 });

        Assert.Equal(HttpStatusCode.NotFound, approveMissing.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingMeeting.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, approveDraft.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, createAgain.StatusCode);
    }

    [Fact]
    public async Task A_cancelled_meeting_cannot_get_minutes()
    {
        var response = await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizer, new { title = "x", mode = "Online" });
        var meeting = (await response.Content.ReadFromJsonAsync<MeetingDto>(Json))!;
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/cancel", _tenantA, _organizer, new { expectedVersion = meeting.Version, reason = "x" });

        var write = await Send(HttpMethod.Put, Url(meeting.Id), _tenantA, _secretary, new { content = "x", expectedVersionNumber = 0 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, write.StatusCode);
    }

    [Fact]
    public async Task Minutes_are_isolated_between_tenants()
    {
        var id = await CreateMeeting();
        await Draft(id);

        var read = await Send(HttpMethod.Get, Url(id), _tenantB, _secretaryB);
        var write = await Send(HttpMethod.Put, Url(id), _tenantB, _secretaryB, new { content = "x", expectedVersionNumber = 0 });
        var submit = await Send(HttpMethod.Post, Url(id, "/submit"), _tenantB, _secretaryB);

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, submit.StatusCode);
    }
}
