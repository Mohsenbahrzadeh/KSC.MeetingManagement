using System.Net;
using System.Net.Http.Json;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class MeetingsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly DateTimeOffset Start = new(2026, 3, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Tenant _tenantA = new(Guid.NewGuid(), "a", "A", "Asia/Tehran");
    private readonly Tenant _tenantB = new(Guid.NewGuid(), "b", "B", "Asia/Tehran");
    private readonly Guid _organizerA = Guid.NewGuid();
    private readonly Guid _organizerB = Guid.NewGuid();
    private readonly Guid _participantA = Guid.NewGuid();
    private readonly HttpClient _client;

    public MeetingsApiTests(WebApplicationFactory<Program> factory)
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

        var tenants = app.Services.GetRequiredService<InMemoryTenantStore>();
        tenants.Add(_tenantA);
        tenants.Add(_tenantB);
        var members = app.Services.GetRequiredService<InMemoryMembershipStore>();
        members.Add(new Membership(_tenantA.Id, _organizerA, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenantA.Id, _participantA, [Roles.Participant]));
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

    private static object NewMeeting(Guid? id = null, string title = "Board", DateTimeOffset? start = null, DateTimeOffset? end = null) =>
        new { id, title, mode = "Hybrid", start, end };

    [Fact]
    public async Task Organizer_creates_and_reads_back_meeting()
    {
        var create = await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizerA, NewMeeting(start: Start, end: Start.AddHours(1)));
        var created = await create.Content.ReadFromJsonAsync<MeetingDto>(Json);

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.Equal(_organizerA, created!.OrganizerId);
        Assert.Equal("Scheduled", created.Status.ToString());

        var read = await Send(HttpMethod.Get, $"/api/v1/meetings/{created.Id}", _tenantA, _participantA);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Participant_cannot_create() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _participantA, NewMeeting())).StatusCode);

    [Fact]
    public async Task Repeated_request_with_same_id_does_not_duplicate()
    {
        var id = Guid.NewGuid();

        var first = await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizerA, NewMeeting(id));
        var second = await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizerA, NewMeeting(id));
        var list = await (await Send(HttpMethod.Get, "/api/v1/meetings", _tenantA, _organizerA)).Content.ReadFromJsonAsync<List<MeetingDto>>(Json);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Single(list!, m => m.Id == id);
    }

    [Fact]
    public async Task Invalid_time_range_is_rejected_with_422() =>
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizerA, NewMeeting(start: Start, end: Start))).StatusCode);

    [Fact]
    public async Task Blank_title_is_rejected_with_422() =>
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizerA, NewMeeting(title: " "))).StatusCode);

    [Fact]
    public async Task Other_tenant_cannot_read_meeting_by_id()
    {
        var created = await (await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizerA, NewMeeting()))
            .Content.ReadFromJsonAsync<MeetingDto>(Json);

        var read = await Send(HttpMethod.Get, $"/api/v1/meetings/{created!.Id}", _tenantB, _organizerB);

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    [Fact]
    public async Task Other_tenant_does_not_see_meetings_in_list()
    {
        var created = await (await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizerA, NewMeeting()))
            .Content.ReadFromJsonAsync<MeetingDto>(Json);

        var list = await (await Send(HttpMethod.Get, "/api/v1/meetings", _tenantB, _organizerB)).Content.ReadFromJsonAsync<List<MeetingDto>>(Json);

        Assert.DoesNotContain(list!, m => m.Id == created!.Id);
    }

    [Fact]
    public async Task Range_query_returns_only_overlapping_meetings()
    {
        var inside = await (await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizerA,
            NewMeeting(title: "inside", start: Start, end: Start.AddHours(1)))).Content.ReadFromJsonAsync<MeetingDto>(Json);
        var outside = await (await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizerA,
            NewMeeting(title: "outside", start: Start.AddDays(40), end: Start.AddDays(40).AddHours(1)))).Content.ReadFromJsonAsync<MeetingDto>(Json);

        var from = Uri.EscapeDataString(Start.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(Start.AddDays(1).ToString("O"));
        var list = await (await Send(HttpMethod.Get, $"/api/v1/meetings?from={from}&to={to}", _tenantA, _organizerA))
            .Content.ReadFromJsonAsync<List<MeetingDto>>(Json);

        Assert.Contains(list!, m => m.Id == inside!.Id);
        Assert.DoesNotContain(list!, m => m.Id == outside!.Id);
    }

    [Fact]
    public async Task Inverted_range_is_rejected() =>
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await Send(HttpMethod.Get, $"/api/v1/meetings?from={Uri.EscapeDataString(Start.ToString("O"))}&to={Uri.EscapeDataString(Start.AddDays(-1).ToString("O"))}", _tenantA, _organizerA)).StatusCode);

    private async Task<MeetingDto> CreateScheduled()
    {
        var create = await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizerA, NewMeeting(start: Start, end: Start.AddHours(1)));
        return (await create.Content.ReadFromJsonAsync<MeetingDto>(Json))!;
    }

    [Fact]
    public async Task Organizer_updates_meeting_and_version_increases()
    {
        var m = await CreateScheduled();

        var response = await Send(HttpMethod.Put, $"/api/v1/meetings/{m.Id}", _tenantA, _organizerA,
            new { expectedVersion = m.Version, title = "New", mode = "Online", start = Start.AddDays(1), end = Start.AddDays(1).AddHours(2) });
        var updated = await response.Content.ReadFromJsonAsync<MeetingDto>(Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("New", updated!.Title);
        Assert.True(updated.Version > m.Version);
    }

    [Fact]
    public async Task Stale_version_returns_conflict()
    {
        var m = await CreateScheduled();
        var body = new { expectedVersion = m.Version, title = "X", mode = "Online" };
        await Send(HttpMethod.Put, $"/api/v1/meetings/{m.Id}", _tenantA, _organizerA, body);

        var stale = await Send(HttpMethod.Put, $"/api/v1/meetings/{m.Id}", _tenantA, _organizerA, body);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Cancel_requires_reason_and_blocks_further_edits()
    {
        var m = await CreateScheduled();

        var noReason = await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/cancel", _tenantA, _organizerA,
            new { expectedVersion = m.Version, reason = " " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);

        var cancel = await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/cancel", _tenantA, _organizerA,
            new { expectedVersion = m.Version, reason = "No quorum" });
        var cancelled = await cancel.Content.ReadFromJsonAsync<MeetingDto>(Json);
        Assert.Equal("Cancelled", cancelled!.Status.ToString());

        var edit = await Send(HttpMethod.Put, $"/api/v1/meetings/{m.Id}", _tenantA, _organizerA,
            new { expectedVersion = cancelled.Version, title = "X", mode = "Online" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);
    }

    [Fact]
    public async Task Postpone_clears_time_and_invalid_transition_is_rejected()
    {
        var m = await CreateScheduled();

        var postpone = await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/postpone", _tenantA, _organizerA, new { expectedVersion = m.Version });
        var postponed = await postpone.Content.ReadFromJsonAsync<MeetingDto>(Json);
        Assert.Equal("Postponed", postponed!.Status.ToString());
        Assert.Null(postponed.StartUtc);

        var again = await Send(HttpMethod.Post, $"/api/v1/meetings/{m.Id}/postpone", _tenantA, _organizerA, new { expectedVersion = postponed.Version });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
    }

    [Fact]
    public async Task Participant_and_other_tenant_cannot_modify()
    {
        var m = await CreateScheduled();
        var body = new { expectedVersion = m.Version, title = "X", mode = "Online" };

        var asParticipant = await Send(HttpMethod.Put, $"/api/v1/meetings/{m.Id}", _tenantA, _participantA, body);
        var otherTenant = await Send(HttpMethod.Put, $"/api/v1/meetings/{m.Id}", _tenantB, _organizerB, body);

        Assert.Equal(HttpStatusCode.Forbidden, asParticipant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherTenant.StatusCode);
    }

    private async Task<string?> CreateNumbered(Tenant tenant, Guid user)
    {
        var response = await Send(HttpMethod.Post, "/api/v1/meetings", tenant, user, NewMeeting());
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MeetingDto>(Json))!.Number;
    }

    [Fact]
    public async Task Concurrent_creates_get_distinct_numbers()
    {
        var numbers = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => CreateNumbered(_tenantA, _organizerA)));

        Assert.All(numbers, n => Assert.Matches(@"^\d{4}-\d{4}$", n!));
        Assert.Equal(20, numbers.Distinct().Count());
    }

    [Fact]
    public async Task Numbers_are_sequential_per_tenant_and_not_shared()
    {
        var a1 = await CreateNumbered(_tenantA, _organizerA);
        var a2 = await CreateNumbered(_tenantA, _organizerA);
        var b1 = await CreateNumbered(_tenantB, _organizerB);

        Assert.NotEqual(a1, a2);
        Assert.EndsWith("-0001", a1);
        Assert.EndsWith("-0002", a2);
        Assert.EndsWith("-0001", b1);
    }

    [Fact]
    public async Task Repeated_create_with_same_id_keeps_the_original_number()
    {
        var id = Guid.NewGuid();
        var first = await (await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizerA, NewMeeting(id))).Content.ReadFromJsonAsync<MeetingDto>(Json);
        var second = await (await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizerA, NewMeeting(id))).Content.ReadFromJsonAsync<MeetingDto>(Json);

        Assert.Equal(first!.Number, second!.Number);
    }
}
