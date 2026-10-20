using System.Net;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class TenantResolutionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly Tenant _active = new(Guid.NewGuid(), "acme", "Acme", "Asia/Tehran");
    private readonly Tenant _suspended = new(Guid.NewGuid(), "frozen", "Frozen", "Asia/Tehran");
    private readonly HttpClient _client;

    public TenantResolutionTests(WebApplicationFactory<Program> factory)
    {
        _suspended.Suspend();
        factory = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.UseInMemoryKscDatabase(Guid.NewGuid().ToString())));
        var store = factory.Services.GetRequiredService<InMemoryTenantStore>();
        store.Add(_active);
        store.Add(_suspended);
        _client = factory.CreateClient(new() { AllowAutoRedirect = false });
    }

    private Task<HttpResponseMessage> Call(string? tenantId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ping/tenant");
        if (tenantId is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Tenant-Id", tenantId);
        }

        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Rejects_missing_tenant() =>
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(null)).StatusCode);

    [Fact]
    public async Task Rejects_malformed_tenant() =>
        Assert.Equal(HttpStatusCode.BadRequest, (await Call("not-a-guid")).StatusCode);

    [Fact]
    public async Task Rejects_unknown_tenant() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(Guid.NewGuid().ToString())).StatusCode);

    [Fact]
    public async Task Rejects_suspended_tenant() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(_suspended.Id.ToString())).StatusCode);

    [Fact]
    public async Task Accepts_active_tenant()
    {
        var response = await Call(_active.Id.ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_active.Id.ToString(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Untagged_endpoints_do_not_require_tenant() =>
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/ping")).StatusCode);
}
