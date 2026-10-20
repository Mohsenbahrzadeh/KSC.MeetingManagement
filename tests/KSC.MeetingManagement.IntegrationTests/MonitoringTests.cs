using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KSC.MeetingManagement.Api.Observability;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class MonitoringTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string MetricsToken = "metrics-token-0123456789";
    private const string MediaSecret = "0123456789abcdef0123456789abcdef-secret";

    private readonly Tenant _tenant = new(Guid.NewGuid(), "a", "A", "UTC");
    private readonly Guid _organizer = Guid.NewGuid();
    private readonly Guid _stranger = Guid.NewGuid();

    private sealed class DownProbe : IDatabaseProbe
    {
        public Task<bool> CanConnectAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class ThrowingProbe : IDatabaseProbe
    {
        public Task<bool> CanConnectAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Host=secret-db;Password=hunter2");
    }

    private HttpClient Client(Dictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings ?? [])
            {
                b.UseSetting(key, value);
            }

            b.ConfigureServices(s =>
            {
                s.UseInMemoryKscDatabase(Guid.NewGuid().ToString());
                s.AddAuthentication(TestAuthHandler.Scheme)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
                services?.Invoke(s);
            });
        });

        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenant);
        var members = app.Services.GetRequiredService<InMemoryMembershipStore>();
        members.Add(new Membership(_tenant.Id, _organizer, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenant.Id, _stranger, [Roles.Participant]));
        return app.CreateClient(new() { AllowAutoRedirect = false });
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Liveness_needs_no_authentication_tenant_or_dependencies()
    {
        var client = Client(services: s => s.AddScoped<IDatabaseProbe, DownProbe>());

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", (await Body(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readiness_is_degraded_but_available_when_only_media_is_not_configured()
    {
        var client = Client();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var body = await Body(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Degraded", body.GetProperty("status").GetString());
        Assert.Equal("Healthy", body.GetProperty("checks").GetProperty("database").GetProperty("status").GetString());
        Assert.Equal("Degraded", body.GetProperty("checks").GetProperty("media").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readiness_is_healthy_when_everything_is_configured()
    {
        var client = Client(new()
        {
            ["Media:ServerUrl"] = "wss://media.example.org",
            ["Media:ApiKey"] = "key",
            ["Media:ApiSecret"] = MediaSecret,
        });

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", (await Body(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_database_outage_turns_readiness_to_503_while_the_process_stays_live()
    {
        var client = Client(services: s => s.AddScoped<IDatabaseProbe, DownProbe>());

        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", (await Body(ready)).GetProperty("checks").GetProperty("database").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task Failure_details_never_reach_the_health_response()
    {
        var client = Client(services: s => s.AddScoped<IDatabaseProbe, ThrowingProbe>());

        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var text = await ready.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-db", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unwritable_storage_is_reported()
    {
        var file = Path.GetTempFileName();
        try
        {
            var client = Client(new() { ["Storage:Root"] = file });

            var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal("Unhealthy", (await Body(ready)).GetProperty("checks").GetProperty("storage").GetProperty("status").GetString());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Metrics_do_not_exist_unless_a_token_is_configured_and_long_enough()
    {
        var none = Client();
        var weak = Client(new() { ["Metrics:Token"] = "short" });

        Assert.Equal(HttpStatusCode.NotFound, (await none.GetAsync(new Uri("/metrics", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await weak.GetAsync(new Uri("/metrics", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Metrics_require_the_bearer_token()
    {
        var client = Client(new() { ["Metrics:Token"] = MetricsToken });

        var anonymous = await client.GetAsync(new Uri("/metrics", UriKind.Relative));
        var wrong = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/metrics") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token-0123456789") } });
        var right = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/metrics") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", MetricsToken) } });

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.StartsWith("text/plain", right.Content.Headers.ContentType!.MediaType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metrics_show_application_and_http_activity_in_prometheus_format()
    {
        var client = Client(new() { ["Metrics:Token"] = MetricsToken, ["RateLimit:SensitivePerMinute"] = "1" });
        var meeting = Guid.NewGuid();

        // A refused room join, a permission denial, then a rate-limited request.
        Assert.Equal(HttpStatusCode.NotFound, (await Post(client, $"/api/v1/meetings/{meeting}/room/join", _organizer)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post(client, $"/api/v1/meetings/{meeting}/room/join", _organizer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(client, "/api/v1/meetings", _stranger)).StatusCode);

        var metrics = await (await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/metrics") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", MetricsToken) } })).Content.ReadAsStringAsync();

        Assert.Contains("# TYPE ksc_room_joins_total counter", metrics, StringComparison.Ordinal);
        Assert.Contains("ksc_room_joins_total{outcome=\"MeetingNotFound\"}", metrics, StringComparison.Ordinal);
        Assert.Contains("ksc_requests_rate_limited_total", metrics, StringComparison.Ordinal);
        Assert.Contains("ksc_authorization_denied_total", metrics, StringComparison.Ordinal);
        Assert.Contains("http_server_request_duration_count", metrics, StringComparison.Ordinal);
        Assert.Contains("dotnet_", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain(_organizer.ToString(), metrics, StringComparison.Ordinal);
        Assert.DoesNotContain(meeting.ToString(), metrics, StringComparison.Ordinal);
    }

    private HttpRequestMessage PostRequest(string url, Guid user)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { }) };
        request.Headers.Add("X-Tenant-Id", _tenant.Id.ToString());
        request.Headers.Add("X-Test-User", user.ToString());
        return request;
    }

    private Task<HttpResponseMessage> Post(HttpClient client, string url, Guid user) => client.SendAsync(PostRequest(url, user));
}
