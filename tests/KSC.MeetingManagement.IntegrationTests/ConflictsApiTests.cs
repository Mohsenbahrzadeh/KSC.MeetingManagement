using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class ConflictsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly DateTimeOffset Start = new(2026, 5, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Tenant _tenantA = new(Guid.NewGuid(), "a", "A", "Asia/Tehran");
    private readonly Tenant _tenantB = new(Guid.NewGuid(), "b", "B", "Asia/Tehran");
    private readonly Guid _org1 = Guid.NewGuid();
    private readonly Guid _org2 = Guid.NewGuid();
    private readonly Guid _orgB = Guid.NewGuid();
    private readonly Guid _member = Guid.NewGuid();
    private readonly HttpClient _client;

    public ConflictsApiTests(WebApplicationFactory<Program> factory)
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
        members.Add(new Membership(_tenantA.Id, _org1, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenantA.Id, _org2, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenantA.Id, _member, [Roles.Participant]));
        members.Add(new Membership(_tenantB.Id, _orgB, [Roles.MeetingOrganizer]));

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

    private Task<HttpResponseMessage> Create(Guid organizer, DateTimeOffset start, DateTimeOffset end, bool allow = false, Tenant? tenant = null) =>
        Send(HttpMethod.Post, "/api/v1/meetings", tenant ?? _tenantA, organizer, new { title = "Board", mode = "Online", start, end, allowConflicts = allow });

    private static async Task<MeetingDto> Read(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<MeetingDto>(Json))!;

    [Fact]
    public async Task Overlapping_meeting_of_the_same_organizer_is_rejected_with_an_understandable_409()
    {
        await Create(_org1, Start, Start.AddHours(2));

        var response = await Create(_org1, Start.AddHours(1), Start.AddHours(3));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("تداخل زمانی", doc.RootElement.GetProperty("title").GetString());
        Assert.Contains("Board", doc.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, doc.RootElement.GetProperty("conflicts").GetArrayLength());
    }

    [Fact]
    public async Task Back_to_back_and_different_organizers_do_not_conflict()
    {
        await Create(_org1, Start, Start.AddHours(1));

        var touching = await Create(_org1, Start.AddHours(1), Start.AddHours(2));
        var otherOrganizer = await Create(_org2, Start, Start.AddHours(1));

        Assert.Equal(HttpStatusCode.Created, touching.StatusCode);
        Assert.Equal(HttpStatusCode.Created, otherOrganizer.StatusCode);
    }

    [Fact]
    public async Task Conflicts_can_be_explicitly_allowed()
    {
        await Create(_org1, Start, Start.AddHours(2));

        var response = await Create(_org1, Start, Start.AddHours(2), allow: true);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Cancelled_meetings_and_other_tenants_never_conflict()
    {
        var first = await Read(await Create(_org1, Start, Start.AddHours(2)));
        await Send(HttpMethod.Post, $"/api/v1/meetings/{first.Id}/cancel", _tenantA, _org1, new { expectedVersion = first.Version, reason = "x" });
        await Create(_orgB, Start, Start.AddHours(2), tenant: _tenantB);

        var sameSlot = await Create(_org1, Start, Start.AddHours(2));

        Assert.Equal(HttpStatusCode.Created, sameSlot.StatusCode);
    }

    [Fact]
    public async Task Moving_a_meeting_onto_another_is_rejected_but_moving_within_itself_is_fine()
    {
        var a = await Read(await Create(_org1, Start, Start.AddHours(1)));
        var b = await Read(await Create(_org1, Start.AddHours(3), Start.AddHours(4)));

        var onto = await Send(HttpMethod.Put, $"/api/v1/meetings/{b.Id}", _tenantA, _org1,
            new { expectedVersion = b.Version, title = "b", mode = "Online", start = Start.AddMinutes(30), end = Start.AddHours(2) });
        var within = await Send(HttpMethod.Put, $"/api/v1/meetings/{a.Id}", _tenantA, _org1,
            new { expectedVersion = a.Version, title = "a", mode = "Online", start = Start.AddMinutes(15), end = Start.AddMinutes(45) });

        Assert.Equal(HttpStatusCode.Conflict, onto.StatusCode);
        Assert.Equal(HttpStatusCode.OK, within.StatusCode);
    }

    [Fact]
    public async Task A_rejected_move_leaves_the_stored_time_unchanged()
    {
        await Create(_org1, Start, Start.AddHours(1));
        var b = await Read(await Create(_org1, Start.AddHours(3), Start.AddHours(4)));

        await Send(HttpMethod.Put, $"/api/v1/meetings/{b.Id}", _tenantA, _org1,
            new { expectedVersion = b.Version, title = "b", mode = "Online", start = Start, end = Start.AddHours(1) });
        var reread = await Read(await Send(HttpMethod.Get, $"/api/v1/meetings/{b.Id}", _tenantA, _org1));

        Assert.Equal(Start.AddHours(3), reread.StartUtc);
        Assert.Equal(b.Version, reread.Version);
    }

    [Fact]
    public async Task Adding_a_participant_who_is_busy_is_rejected()
    {
        var a = await Read(await Create(_org1, Start, Start.AddHours(1)));
        var b = await Read(await Create(_org2, Start.AddMinutes(30), Start.AddHours(2)));
        var busy = await Send(HttpMethod.Post, $"/api/v1/meetings/{a.Id}/participants/internal", _tenantA, _org1, new { userId = _member });
        Assert.Equal(HttpStatusCode.Created, busy.StatusCode);

        var conflict = await Send(HttpMethod.Post, $"/api/v1/meetings/{b.Id}/participants/internal", _tenantA, _org2, new { userId = _member });
        var forced = await Send(HttpMethod.Post, $"/api/v1/meetings/{b.Id}/participants/internal", _tenantA, _org2, new { userId = _member, allowConflicts = true });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.Created, forced.StatusCode);
    }

    [Fact]
    public async Task Rescheduling_checks_existing_participants_too()
    {
        var a = await Read(await Create(_org1, Start, Start.AddHours(1)));
        var b = await Read(await Create(_org2, Start.AddHours(5), Start.AddHours(6)));
        await Send(HttpMethod.Post, $"/api/v1/meetings/{a.Id}/participants/internal", _tenantA, _org1, new { userId = _member });
        await Send(HttpMethod.Post, $"/api/v1/meetings/{b.Id}/participants/internal", _tenantA, _org2, new { userId = _member });

        var move = await Send(HttpMethod.Put, $"/api/v1/meetings/{b.Id}", _tenantA, _org2,
            new { expectedVersion = b.Version, title = "b", mode = "Online", start = Start, end = Start.AddHours(1) });

        Assert.Equal(HttpStatusCode.Conflict, move.StatusCode);
    }

    [Fact]
    public async Task Reschedule_moves_only_the_time_and_checks_version_and_conflicts()
    {
        var a = await Read(await Create(_org1, Start, Start.AddHours(1)));
        var b = await Read(await Create(_org1, Start.AddHours(3), Start.AddHours(4)));

        var ok = await Send(HttpMethod.Post, $"/api/v1/meetings/{a.Id}/reschedule", _tenantA, _org1,
            new { expectedVersion = a.Version, start = Start.AddHours(6), end = Start.AddHours(7) });
        var moved = await Read(ok);
        var stale = await Send(HttpMethod.Post, $"/api/v1/meetings/{a.Id}/reschedule", _tenantA, _org1,
            new { expectedVersion = a.Version, start = Start.AddHours(8), end = Start.AddHours(9) });
        var onto = await Send(HttpMethod.Post, $"/api/v1/meetings/{b.Id}/reschedule", _tenantA, _org1,
            new { expectedVersion = b.Version, start = Start.AddHours(6), end = Start.AddHours(7) });
        var invalid = await Send(HttpMethod.Post, $"/api/v1/meetings/{b.Id}/reschedule", _tenantA, _org1,
            new { expectedVersion = b.Version, start = Start.AddHours(8), end = Start.AddHours(8) });

        Assert.Equal(Start.AddHours(6), moved.StartUtc);
        Assert.Equal(a.Title, moved.Title);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, onto.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
    }

    [Fact]
    public async Task Reschedule_requires_edit_permission_and_is_tenant_scoped()
    {
        var a = await Read(await Create(_org1, Start, Start.AddHours(1)));
        var body = new { expectedVersion = a.Version, start = Start.AddHours(6), end = Start.AddHours(7) };

        var participant = await Send(HttpMethod.Post, $"/api/v1/meetings/{a.Id}/reschedule", _tenantA, _member, body);
        var otherTenant = await Send(HttpMethod.Post, $"/api/v1/meetings/{a.Id}/reschedule", _tenantB, _orgB, body);

        Assert.Equal(HttpStatusCode.Forbidden, participant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherTenant.StatusCode);
    }
}
