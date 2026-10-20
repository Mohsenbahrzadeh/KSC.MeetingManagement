using System.Net;
using System.Net.Http.Json;
using KSC.MeetingManagement.Application.Agenda;
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

public sealed class ResolutionsApiTests : IClassFixture<WebApplicationFactory<Program>>
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

    public ResolutionsApiTests(WebApplicationFactory<Program> factory)
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

    private async Task<AgendaDto> AddTopic(Guid meetingId, string title = "موضوع")
    {
        var agenda = (await (await Send(HttpMethod.Get, $"/api/v1/meetings/{meetingId}/agenda", _tenantA, _organizer)).Content.ReadFromJsonAsync<AgendaDto>(Json))!;
        var response = await Send(HttpMethod.Post, $"/api/v1/meetings/{meetingId}/agenda", _tenantA, _organizer, new { expectedRevision = agenda.Revision, title });
        return (await response.Content.ReadFromJsonAsync<AgendaDto>(Json))!;
    }

    private static string Url(Guid id, string suffix = "") => $"/api/v1/meetings/{id}/resolutions{suffix}";

    private static async Task<ResolutionDto> Read(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ResolutionDto>(Json))!;
    }

    private async Task<ResolutionDto> Create(Guid meetingId, string text = "مصوبه", Guid? topic = null) =>
        await Read(await Send(HttpMethod.Post, Url(meetingId), _tenantA, _secretary, new { text, agendaItemId = topic }));

    [Fact]
    public async Task Resolutions_get_sequential_numbers_per_meeting_and_link_to_a_valid_topic()
    {
        var meetingId = await CreateMeeting();
        var other = await CreateMeeting();
        var agenda = await AddTopic(meetingId);
        var topic = agenda.Items[0].Id;

        var first = await Create(meetingId, "الف", topic);
        var second = await Create(meetingId, "ب");
        var otherFirst = await Create(other, "ج");

        Assert.Equal((1, 2, 1), (first.Number, second.Number, otherFirst.Number));
        Assert.Equal(topic, first.AgendaItemId);
        Assert.Equal(ResolutionStatus.Draft, first.Status);
        var list = (await (await Send(HttpMethod.Get, Url(meetingId), _tenantA, _participant)).Content.ReadFromJsonAsync<List<ResolutionDto>>(Json))!;
        Assert.Equal([1, 2], list.Select(r => r.Number));
    }

    [Fact]
    public async Task A_topic_from_another_meeting_or_a_missing_topic_is_rejected()
    {
        var meetingId = await CreateMeeting();
        var other = await CreateMeeting();
        var foreignTopic = (await AddTopic(other)).Items[0].Id;

        var foreign = await Send(HttpMethod.Post, Url(meetingId), _tenantA, _secretary, new { text = "x", agendaItemId = foreignTopic });
        var unknown = await Send(HttpMethod.Post, Url(meetingId), _tenantA, _secretary, new { text = "x", agendaItemId = Guid.NewGuid() });
        var blank = await Send(HttpMethod.Post, Url(meetingId), _tenantA, _secretary, new { text = " " });
        var missingMeeting = await Send(HttpMethod.Post, Url(Guid.NewGuid()), _tenantA, _secretary, new { text = "x" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingMeeting.StatusCode);
    }

    [Fact]
    public async Task Draft_can_be_edited_then_adopted_and_adopted_text_is_fixed()
    {
        var meetingId = await CreateMeeting();
        var draft = await Create(meetingId, "پیش‌نویس");

        var edited = await Read(await Send(HttpMethod.Put, Url(meetingId, $"/{draft.Id}"), _tenantA, _secretary,
            new { expectedLockVersion = draft.LockVersion, text = "نهایی" }));
        var adopted = await Read(await Send(HttpMethod.Post, Url(meetingId, $"/{draft.Id}/adopt"), _tenantA, _secretary,
            new { expectedLockVersion = edited.LockVersion }));
        var editAfter = await Send(HttpMethod.Put, Url(meetingId, $"/{draft.Id}"), _tenantA, _secretary,
            new { expectedLockVersion = adopted.LockVersion, text = "دست‌کاری" });

        Assert.Equal("نهایی", adopted.Text);
        Assert.Equal(ResolutionStatus.Adopted, adopted.Status);
        Assert.Equal(_secretary, adopted.DecidedByUserId);
        Assert.NotNull(adopted.DecidedAtUtc);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, editAfter.StatusCode);
    }

    [Fact]
    public async Task Rejected_is_terminal_and_stale_versions_conflict()
    {
        var meetingId = await CreateMeeting();
        var draft = await Create(meetingId);

        var rejected = await Read(await Send(HttpMethod.Post, Url(meetingId, $"/{draft.Id}/reject"), _tenantA, _secretary, new { expectedLockVersion = draft.LockVersion }));
        var adoptAfter = await Send(HttpMethod.Post, Url(meetingId, $"/{draft.Id}/adopt"), _tenantA, _secretary, new { expectedLockVersion = rejected.LockVersion });
        var stale = await Send(HttpMethod.Post, Url(meetingId, $"/{draft.Id}/adopt"), _tenantA, _secretary, new { expectedLockVersion = draft.LockVersion });

        Assert.Equal(ResolutionStatus.Rejected, rejected.Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, adoptAfter.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Revocation_needs_the_approver_and_a_reason()
    {
        var meetingId = await CreateMeeting();
        var draft = await Create(meetingId);
        var adopted = await Read(await Send(HttpMethod.Post, Url(meetingId, $"/{draft.Id}/adopt"), _tenantA, _secretary, new { expectedLockVersion = draft.LockVersion }));
        var url = Url(meetingId, $"/{draft.Id}/revoke");

        var bySecretary = await Send(HttpMethod.Post, url, _tenantA, _secretary, new { expectedLockVersion = adopted.LockVersion, reason = "x" });
        var noReason = await Send(HttpMethod.Post, url, _tenantA, _approver, new { expectedLockVersion = adopted.LockVersion, reason = " " });
        var revoked = await Read(await Send(HttpMethod.Post, url, _tenantA, _approver, new { expectedLockVersion = adopted.LockVersion, reason = "منسوخ شد" }));

        Assert.Equal(HttpStatusCode.Forbidden, bySecretary.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        Assert.Equal(ResolutionStatus.Revoked, revoked.Status);
        Assert.Equal("منسوخ شد", revoked.RevocationReason);
        Assert.Equal(_approver, revoked.DecidedByUserId);
    }

    [Fact]
    public async Task A_topic_with_resolutions_cannot_be_removed_from_the_agenda()
    {
        var meetingId = await CreateMeeting();
        var agenda = await AddTopic(meetingId);
        var plain = await AddTopic(meetingId, "بدون مصوبه");
        await Create(meetingId, "x", agenda.Items[0].Id);

        var blocked = await Send(HttpMethod.Delete, $"/api/v1/meetings/{meetingId}/agenda/{agenda.Items[0].Id}?expectedRevision={plain.Revision}", _tenantA, _organizer);
        var allowed = await Send(HttpMethod.Delete, $"/api/v1/meetings/{meetingId}/agenda/{plain.Items[1].Id}?expectedRevision={plain.Revision}", _tenantA, _organizer);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Roles_and_tenants_are_enforced()
    {
        var meetingId = await CreateMeeting();
        var draft = await Create(meetingId);

        var participantCreate = await Send(HttpMethod.Post, Url(meetingId), _tenantA, _participant, new { text = "x" });
        var organizerCreate = await Send(HttpMethod.Post, Url(meetingId), _tenantA, _organizer, new { text = "x" });
        var otherTenantRead = await Send(HttpMethod.Get, Url(meetingId), _tenantB, _secretaryB);
        var otherTenantAdopt = await Send(HttpMethod.Post, Url(meetingId, $"/{draft.Id}/adopt"), _tenantB, _secretaryB, new { expectedLockVersion = draft.LockVersion });

        Assert.Equal(HttpStatusCode.Forbidden, participantCreate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, organizerCreate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherTenantRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherTenantAdopt.StatusCode);
    }

    [Fact]
    public async Task A_cancelled_meeting_cannot_get_resolutions()
    {
        var response = await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizer, new { title = "x", mode = "Online" });
        var meeting = (await response.Content.ReadFromJsonAsync<MeetingDto>(Json))!;
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/cancel", _tenantA, _organizer, new { expectedVersion = meeting.Version, reason = "x" });

        var create = await Send(HttpMethod.Post, Url(meeting.Id), _tenantA, _secretary, new { text = "x" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
    }
}
