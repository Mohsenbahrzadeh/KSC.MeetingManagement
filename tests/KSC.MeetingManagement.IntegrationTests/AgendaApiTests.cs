using System.Net;
using System.Net.Http.Json;
using KSC.MeetingManagement.Application.Agenda;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class AgendaApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Tenant _tenantA = new(Guid.NewGuid(), "a", "A", "Asia/Tehran");
    private readonly Tenant _tenantB = new(Guid.NewGuid(), "b", "B", "Asia/Tehran");
    private readonly Guid _organizer = Guid.NewGuid();
    private readonly Guid _participant = Guid.NewGuid();
    private readonly Guid _organizerB = Guid.NewGuid();
    private readonly Guid _outsider = Guid.NewGuid();
    private readonly HttpClient _client;

    public AgendaApiTests(WebApplicationFactory<Program> factory)
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
        members.Add(new Membership(_tenantA.Id, _participant, [Roles.Participant]));
        members.Add(new Membership(_tenantB.Id, _organizerB, [Roles.MeetingOrganizer]));

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

    private async Task<AgendaDto> Add(Guid meetingId, long revision, string title, Guid? presenter = null)
    {
        var response = await Send(HttpMethod.Post, $"/api/v1/meetings/{meetingId}/agenda", _tenantA, _organizer,
            new { expectedRevision = revision, title, presenterUserId = presenter, durationMinutes = 15 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AgendaDto>(Json))!;
    }

    private async Task<AgendaDto> Read(Guid meetingId, Tenant? tenant = null, Guid? user = null) =>
        (await (await Send(HttpMethod.Get, $"/api/v1/meetings/{meetingId}/agenda", tenant ?? _tenantA, user ?? _participant)).Content.ReadFromJsonAsync<AgendaDto>(Json))!;

    [Fact]
    public async Task Items_are_appended_in_order_and_everyone_can_read_them()
    {
        var id = await CreateMeeting();
        var a1 = await Add(id, 0, "اول");
        var a2 = await Add(id, a1.Revision, "دوم");
        await Add(id, a2.Revision, "سوم");

        var agenda = await Read(id);

        Assert.Equal(["اول", "دوم", "سوم"], agenda.Items.Select(i => i.Title));
        Assert.Equal([1, 2, 3], agenda.Items.Select(i => i.Order));
        Assert.Equal(3, agenda.Revision);
    }

    [Fact]
    public async Task Reordering_persists_and_requires_every_item_exactly_once()
    {
        var id = await CreateMeeting();
        var a = await Add(id, 0, "الف");
        var b = await Add(id, a.Revision, "ب");
        var items = b.Items;

        var bad = await Send(HttpMethod.Put, $"/api/v1/meetings/{id}/agenda/order", _tenantA, _organizer,
            new { expectedRevision = b.Revision, itemIds = new[] { items[0].Id } });
        var ok = await Send(HttpMethod.Put, $"/api/v1/meetings/{id}/agenda/order", _tenantA, _organizer,
            new { expectedRevision = b.Revision, itemIds = new[] { items[1].Id, items[0].Id } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(["ب", "الف"], (await Read(id)).Items.Select(i => i.Title));
    }

    [Fact]
    public async Task Removing_an_item_closes_the_gap()
    {
        var id = await CreateMeeting();
        var a = await Add(id, 0, "الف");
        var b = await Add(id, a.Revision, "ب");
        var c = await Add(id, b.Revision, "ج");

        var remove = await Send(HttpMethod.Delete, $"/api/v1/meetings/{id}/agenda/{c.Items[1].Id}?expectedRevision={c.Revision}", _tenantA, _organizer);

        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        var agenda = await Read(id);
        Assert.Equal(["الف", "ج"], agenda.Items.Select(i => i.Title));
        Assert.Equal([1, 2], agenda.Items.Select(i => i.Order));
    }

    [Fact]
    public async Task A_stale_revision_is_a_conflict()
    {
        var id = await CreateMeeting();
        var first = await Add(id, 0, "الف");
        await Add(id, first.Revision, "ب");

        var stale = await Send(HttpMethod.Post, $"/api/v1/meetings/{id}/agenda", _tenantA, _organizer,
            new { expectedRevision = first.Revision, title = "ج" });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(2, (await Read(id)).Items.Count);
    }

    [Fact]
    public async Task Update_changes_fields_and_validates_the_presenter()
    {
        var id = await CreateMeeting();
        var a = await Add(id, 0, "الف");
        var url = $"/api/v1/meetings/{id}/agenda/{a.Items[0].Id}";

        var stranger = await Send(HttpMethod.Put, url, _tenantA, _organizer,
            new { expectedRevision = a.Revision, title = "الف۲", presenterUserId = _outsider });
        var ok = await Send(HttpMethod.Put, url, _tenantA, _organizer,
            new { expectedRevision = a.Revision, title = "الف۲", presenterUserId = _participant, durationMinutes = 20 });
        var updated = (await ok.Content.ReadFromJsonAsync<AgendaDto>(Json))!;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, stranger.StatusCode);
        Assert.Equal("الف۲", updated.Items[0].Title);
        Assert.Equal(_participant, updated.Items[0].PresenterUserId);
        Assert.Equal(20, updated.Items[0].DurationMinutes);
    }

    [Fact]
    public async Task Invalid_input_and_unknown_items_are_rejected()
    {
        var id = await CreateMeeting();

        var blank = await Send(HttpMethod.Post, $"/api/v1/meetings/{id}/agenda", _tenantA, _organizer, new { expectedRevision = 0, title = " " });
        var unknown = await Send(HttpMethod.Put, $"/api/v1/meetings/{id}/agenda/{Guid.NewGuid()}", _tenantA, _organizer,
            new { expectedRevision = 0, title = "x" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_cancelled_meeting_agenda_cannot_change()
    {
        var response = await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizer, new { title = "x", mode = "Online" });
        var meeting = (await response.Content.ReadFromJsonAsync<MeetingDto>(Json))!;
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/cancel", _tenantA, _organizer, new { expectedVersion = meeting.Version, reason = "x" });

        var add = await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/agenda", _tenantA, _organizer, new { expectedRevision = 0, title = "t" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, add.StatusCode);
    }

    [Fact]
    public async Task Participants_cannot_edit_and_other_tenants_cannot_see_the_agenda()
    {
        var id = await CreateMeeting();
        await Add(id, 0, "سری");

        var edit = await Send(HttpMethod.Post, $"/api/v1/meetings/{id}/agenda", _tenantA, _participant, new { expectedRevision = 1, title = "x" });
        var other = await Send(HttpMethod.Get, $"/api/v1/meetings/{id}/agenda", _tenantB, _organizerB);
        var otherEdit = await Send(HttpMethod.Post, $"/api/v1/meetings/{id}/agenda", _tenantB, _organizerB, new { expectedRevision = 1, title = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherEdit.StatusCode);
    }
}
