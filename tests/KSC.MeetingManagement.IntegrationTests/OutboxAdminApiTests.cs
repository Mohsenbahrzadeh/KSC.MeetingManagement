using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Notifications;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Persistence;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class OutboxAdminApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly WebApplicationFactory<Program> _app;
    private readonly Tenant _a = new(Guid.NewGuid(), "a", "A", "UTC");
    private readonly Tenant _b = new(Guid.NewGuid(), "b", "B", "UTC");
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _participant = Guid.NewGuid();
    private readonly Guid _adminB = Guid.NewGuid();
    private readonly Guid _deadA1 = Guid.NewGuid();
    private readonly Guid _deadA2 = Guid.NewGuid();
    private readonly Guid _deadB = Guid.NewGuid();
    private readonly Guid _pendingA = Guid.NewGuid();
    private readonly HttpClient _client;

    public OutboxAdminApiTests(WebApplicationFactory<Program> factory)
    {
        _app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.UseInMemoryKscDatabase(Guid.NewGuid().ToString());
            s.AddSingleton<TimeProvider>(new FixedClock(Now));
            s.AddAuthentication(TestAuthHandler.Scheme).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
        }));
        _app.Services.GetRequiredService<InMemoryTenantStore>().Add(_a);
        _app.Services.GetRequiredService<InMemoryTenantStore>().Add(_b);
        var members = _app.Services.GetRequiredService<InMemoryMembershipStore>();
        members.Add(new Membership(_a.Id, _admin, [Roles.TenantAdministrator]));
        members.Add(new Membership(_a.Id, _participant, [Roles.Participant]));
        members.Add(new Membership(_b.Id, _adminB, [Roles.TenantAdministrator]));

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KscDbContext>();
        db.OutboxMessages.AddRange(
            Dead(_a, _deadA1, "k1"), Dead(_a, _deadA2, "k2"), Dead(_b, _deadB, "k3"),
            Message(_a, _pendingA, "k4"));
        db.SaveChanges();
        _client = _app.CreateClient(new() { AllowAutoRedirect = false });
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static OutboxMessage Message(Tenant tenant, Guid id, string key) =>
        new(id, tenant.Id, NotificationKind.MeetingReminder, RecipientType.Guest, null, "guest@example.com", "Guest", "یادآوری", "P:secret-body", key, Now.AddHours(-3), Now.AddHours(-3));

    private static OutboxMessage Dead(Tenant tenant, Guid id, string key)
    {
        var message = Message(tenant, id, key);
        message.MarkFailed(Now.AddHours(-2), "smtp refused", maxAttempts: 1, TimeSpan.FromMinutes(1));
        return message;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, Tenant tenant, Guid user)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Tenant-Id", tenant.Id.ToString());
        request.Headers.Add("X-Test-User", user.ToString());
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Stats_count_only_the_organisation_of_the_caller()
    {
        var json = await (await Send(HttpMethod.Get, "/api/v1/notifications/outbox/stats", _a, _admin)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, json.GetProperty("pending").GetInt32());
        Assert.Equal(2, json.GetProperty("dead").GetInt32());
        Assert.Equal(3 * 3600, json.GetProperty("oldestPendingAgeSeconds").GetDouble());
    }

    [Fact]
    public async Task Dead_messages_are_listed_without_text_or_address_and_only_for_this_organisation()
    {
        var response = await Send(HttpMethod.Get, "/api/v1/notifications/outbox/dead", _a, _admin);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_deadA1.ToString(), body, StringComparison.Ordinal);
        Assert.Contains("smtp refused", body, StringComparison.Ordinal);
        Assert.DoesNotContain(_deadB.ToString(), body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-body", body, StringComparison.Ordinal);
        Assert.DoesNotContain("guest@example.com", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dead_message_can_be_requeued_but_not_one_of_another_organisation_or_a_pending_one()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, $"/api/v1/notifications/outbox/dead/{_deadA1}/requeue", _a, _admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"/api/v1/notifications/outbox/dead/{_deadA1}/requeue", _a, _admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"/api/v1/notifications/outbox/dead/{_deadB}/requeue", _a, _admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"/api/v1/notifications/outbox/dead/{_pendingA}/requeue", _a, _admin)).StatusCode);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KscDbContext>();
        var again = await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.Id == _deadA1);
        Assert.Equal(OutboxStatus.Pending, again.Status);
        Assert.Equal(0, again.Attempts);
        Assert.Equal(OutboxStatus.Dead, (await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.Id == _deadB)).Status);
    }

    [Fact]
    public async Task Requeue_all_moves_every_dead_message_of_this_organisation_only()
    {
        var response = await Send(HttpMethod.Post, "/api/v1/notifications/outbox/dead/requeue-all", _a, _admin);

        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requeued").GetInt32());
        var left = await (await Send(HttpMethod.Get, "/api/v1/notifications/outbox/stats", _b, _adminB)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, left.GetProperty("dead").GetInt32());
    }

    [Fact]
    public async Task Members_without_organisation_management_are_refused()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/api/v1/notifications/outbox/stats", _a, _participant)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"/api/v1/notifications/outbox/dead/{_deadA1}/requeue", _a, _participant)).StatusCode);
    }
}
