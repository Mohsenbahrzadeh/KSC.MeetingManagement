using KSC.MeetingManagement.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace KSC.MeetingManagement.UnitTests;

public sealed class CircuitMetricsTests
{
    [Fact]
    public void Active_circuit_count_tracks_opens_and_closes_without_becoming_negative()
    {
        var metrics = new CircuitMetrics();

        metrics.CircuitClosed();
        Assert.Equal(0, metrics.ActiveCircuits);

        metrics.CircuitOpened();
        metrics.CircuitOpened();
        Assert.Equal(2, metrics.ActiveCircuits);

        metrics.CircuitClosed();
        metrics.CircuitClosed();
        metrics.CircuitClosed();
        Assert.Equal(0, metrics.ActiveCircuits);
    }

    [Fact]
    public void Circuit_handler_updates_the_shared_active_count()
    {
        var metrics = new CircuitMetrics();
        var handler = new CircuitMetricsHandler(metrics);
        handler.CircuitOpened(null!);
        Assert.Equal(1, metrics.ActiveCircuits);

        handler.CircuitClosed(null!);
        Assert.Equal(0, metrics.ActiveCircuits);
    }

    [Fact]
    public async Task Metrics_endpoint_is_absent_without_a_valid_length_token()
    {
        using var app = await StartWebAsync("short");
        using var client = Client(app);

        Assert.Equal(StatusCodes.Status404NotFound, (int)(await client.GetAsync("/metrics")).StatusCode);
    }

    [Fact]
    public async Task Metrics_endpoint_requires_bearer_token_and_exports_active_circuits()
    {
        const string token = "metrics-token-0123456789";
        using var app = await StartWebAsync(token);
        using var client = Client(app);
        app.Services.GetRequiredService<CircuitMetrics>().CircuitOpened();

        Assert.Equal(StatusCodes.Status401Unauthorized, (int)(await client.GetAsync("/metrics")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(StatusCodes.Status200OK, (int)response.StatusCode);
        Assert.Contains("# TYPE ksc_blazor_active_circuits gauge", content, StringComparison.Ordinal);
        Assert.Contains("ksc_blazor_active_circuits 1", content, StringComparison.Ordinal);
    }

    private static HttpClient Client(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private static async Task<WebApplication> StartWebAsync(string token)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Metrics:Token"] = token;
        builder.Services.AddSingleton<CircuitMetrics>();
        var app = builder.Build();
        app.MapKscWebMetrics(app.Configuration);
        await app.StartAsync();
        return app;
    }
}
