using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class CorrelationIdTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient(new() { AllowAutoRedirect = false });

    [Fact]
    public async Task Generates_correlation_id_when_missing()
    {
        var response = await _client.GetAsync("/api/v1/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var values));
        Assert.False(string.IsNullOrWhiteSpace(values!.Single()));
    }

    [Fact]
    public async Task Echoes_valid_incoming_correlation_id()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ping");
        request.Headers.Add("X-Correlation-Id", "abc-123");

        var response = await _client.SendAsync(request);

        Assert.Equal("abc-123", response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Replaces_unsafe_incoming_correlation_id()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ping");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "bad value with spaces");

        var response = await _client.SendAsync(request);

        Assert.NotEqual("bad value with spaces", response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Unhandled_error_returns_problem_details_with_same_correlation_id()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ping/fail");
        request.Headers.Add("X-Correlation-Id", "trace-1");

        var response = await _client.SendAsync(request);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("trace-1", json.RootElement.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("ping failure test", json.RootElement.ToString());
    }
}
