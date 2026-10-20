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
}
