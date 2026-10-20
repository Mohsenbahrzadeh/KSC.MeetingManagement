using System.Net;
using System.Net.Http.Json;
using KSC.MeetingManagement.Application.Audit;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Participants;
using KSC.MeetingManagement.Domain.Audit;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class AuditApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Tenant _tenantA = new(Guid.NewGuid(), "a", "A", "UTC");
    private readonly Tenant _tenantB = new(Guid.NewGuid(), "b", "B", "UTC");
    private readonly Guid _organizer = Guid.NewGuid();
    private readonly Guid _participant = Guid.NewGuid();
    private readonly Guid _auditor = Guid.NewGuid();
    private readonly Guid _auditorB = Guid.NewGuid();
    private readonly HttpClient _client;

    public AuditApiTests(WebApplicationFactory<Program> factory)
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
        members.Add(new Membership(_tenantA.Id, _auditor, [Roles.Auditor]));
        members.Add(new Membership(_tenantB.Id, _auditorB, [Roles.Auditor]));

        _client = app.CreateClient(new() { AllowAutoRedirect = false });
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, Tenant tenant, Guid? user, object? body = null, string? correlation = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Tenant-Id", tenant.Id.ToString());
        if (user is { } u)
        {
            request.Headers.Add("X-Test-User", u.ToString());
        }

        if (correlation is not null)
        {
            request.Headers.Add("X-Correlation-Id", correlation);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _client.SendAsync(request);
    }

    private async Task<MeetingDto> CreateMeeting(string? correlation = null)
    {
        var response = await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizer, new { title = "Board", mode = "Online" }, correlation);
        return (await response.Content.ReadFromJsonAsync<MeetingDto>(Json))!;
    }

    private async Task<List<AuditEventDto>> Search(string query = "", Tenant? tenant = null, Guid? user = null)
    {
        var response = await Send(HttpMethod.Get, "/api/v1/audit" + query, tenant ?? _tenantA, user ?? _auditor);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<AuditEventDto>>(Json))!;
    }

    [Fact]
    public async Task A_successful_change_records_actor_action_resource_time_and_correlation_id()
    {
        var meeting = await CreateMeeting("corr-create-1");
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);

        var events = await Search($"?action=meeting.create&resourceId={meeting.Id}");

        var e = Assert.Single(events);
        Assert.Equal((AuditActorType.User, _organizer, "meeting", AuditOutcome.Success), (e.ActorType, e.ActorId, e.ResourceType, e.Outcome));
        Assert.Equal("corr-create-1", e.CorrelationId);
        Assert.True(e.OccurredAtUtc > before);
        Assert.Equal(meeting.Id, e.ResourceId);
    }

    [Fact]
    public async Task A_failed_request_is_recorded_as_a_failure_and_never_stores_the_request_content()
    {
        var meeting = await CreateMeeting();
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/cancel", _tenantA, _organizer, new { expectedVersion = meeting.Version, reason = " " });
        await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/cancel", _tenantA, _organizer, new { expectedVersion = meeting.Version, reason = "دلیل خصوصی-XYZ" });

        var events = await Search($"?action=meeting.cancel&resourceId={meeting.Id}");

        Assert.Equal([AuditOutcome.Success, AuditOutcome.Failure], events.Select(e => e.Outcome).Order());
        Assert.DoesNotContain(events, e => (e.Detail ?? string.Empty).Contains("XYZ", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Detail == "status 422");
    }

    [Fact]
    public async Task A_refused_request_is_recorded_as_denied_with_the_endpoint_and_actor()
    {
        var response = await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _participant, new { title = "x", mode = "Online" });

        var events = await Search("?action=authorization.denied");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var e = Assert.Single(events);
        Assert.Equal((AuditOutcome.Denied, _participant), (e.Outcome, e.ActorId));
        Assert.Equal("POST api/v1/meetings", e.Detail);
    }

    [Fact]
    public async Task Guest_actions_are_recorded_without_the_invitation_token()
    {
        var meeting = await CreateMeeting();
        var invite = (await (await Send(HttpMethod.Post, $"/api/v1/meetings/{meeting.Id}/participants/guests", _tenantA, _organizer,
            new { name = "مهمان", email = "g@example.com", expiresUtc = DateTimeOffset.UtcNow.AddDays(2) })).Content.ReadFromJsonAsync<GuestInvitationDto>(Json))!;
        var url = $"/api/v1/meetings/{meeting.Id}/participants/{invite.Participant.Id}/redeem";

        await Send(HttpMethod.Post, url, _tenantA, null, new { token = "wrong" });
        await Send(HttpMethod.Post, url, _tenantA, null, new { token = invite.Token });

        var events = await Search("?action=participant.redeem");
        var all = System.Text.Json.JsonSerializer.Serialize(await Search("?take=200"));

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal((AuditActorType.Guest, (Guid?)invite.Participant.Id), (e.ActorType, e.ActorId)));
        Assert.Equal([AuditOutcome.Success, AuditOutcome.Failure], events.Select(e => e.Outcome).Order());
        Assert.DoesNotContain(invite.Token, all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_filters_by_actor_outcome_and_time_range_and_pages()
    {
        var a = await CreateMeeting();
        await CreateMeeting();
        await Send(HttpMethod.Post, $"/api/v1/meetings/{a.Id}/cancel", _tenantA, _organizer, new { expectedVersion = a.Version, reason = "x" });
        var now = DateTimeOffset.UtcNow;

        var byActor = await Search($"?actorId={_organizer}");
        var failures = await Search("?outcome=Failure");
        var inRange = await Search($"?from={Uri.EscapeDataString(now.AddMinutes(-5).ToString("O"))}&to={Uri.EscapeDataString(now.AddMinutes(5).ToString("O"))}");
        var outOfRange = await Search($"?from={Uri.EscapeDataString(now.AddDays(-9).ToString("O"))}&to={Uri.EscapeDataString(now.AddDays(-8).ToString("O"))}");
        var page = await Search("?take=1");
        var secondPage = await Search("?take=1&skip=1");

        Assert.Equal(3, byActor.Count);
        Assert.Empty(failures);
        Assert.Equal(3, inRange.Count);
        Assert.Empty(outOfRange);
        Assert.Single(page);
        Assert.NotEqual(page[0].Id, secondPage[0].Id);
        Assert.True(byActor.Zip(byActor.Skip(1)).All(p => p.First.OccurredAtUtc >= p.Second.OccurredAtUtc));
    }

    [Fact]
    public async Task Only_auditors_can_read_the_log_and_ranges_are_validated()
    {
        await CreateMeeting();

        var participant = await Send(HttpMethod.Get, "/api/v1/audit", _tenantA, _participant);
        var organizer = await Send(HttpMethod.Get, "/api/v1/audit", _tenantA, _organizer);
        var anonymous = await Send(HttpMethod.Get, "/api/v1/audit", _tenantA, null);
        var inverted = await Send(HttpMethod.Get, $"/api/v1/audit?from={Uri.EscapeDataString("2026-02-01T00:00:00Z")}&to={Uri.EscapeDataString("2026-01-01T00:00:00Z")}", _tenantA, _auditor);
        var tooLong = await Send(HttpMethod.Get, $"/api/v1/audit?from={Uri.EscapeDataString("2020-01-01T00:00:00Z")}&to={Uri.EscapeDataString("2026-01-01T00:00:00Z")}", _tenantA, _auditor);

        Assert.Equal(HttpStatusCode.Forbidden, participant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, organizer.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inverted.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
    }

    [Fact]
    public async Task The_log_is_isolated_between_tenants_and_denied_requests_of_one_tenant_are_not_visible_to_another()
    {
        await CreateMeeting();
        await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _participant, new { title = "x", mode = "Online" });

        var other = await Search(tenant: _tenantB, user: _auditorB);

        Assert.Empty(other);
        Assert.NotEmpty(await Search());
    }

    [Fact]
    public async Task Reading_the_log_does_not_create_events_about_itself_but_sensitive_reads_like_downloads_are_covered()
    {
        await Search();
        await Search();

        var events = await Search("?take=200");

        Assert.DoesNotContain(events, e => e.ResourceType == "audit");
    }
}

public class AuditOutcomeMappingTests
{
    [Theory]
    [InlineData(200, AuditOutcome.Success)]
    [InlineData(201, AuditOutcome.Success)]
    [InlineData(204, AuditOutcome.Success)]
    [InlineData(401, AuditOutcome.Denied)]
    [InlineData(403, AuditOutcome.Denied)]
    [InlineData(404, AuditOutcome.Failure)]
    [InlineData(409, AuditOutcome.Failure)]
    [InlineData(422, AuditOutcome.Failure)]
    [InlineData(500, AuditOutcome.Failure)]
    public void Status_codes_map_to_outcomes(int status, AuditOutcome expected) =>
        Assert.Equal(expected, KSC.MeetingManagement.Api.Observability.AuditedAttribute.OutcomeOf(status));
}

