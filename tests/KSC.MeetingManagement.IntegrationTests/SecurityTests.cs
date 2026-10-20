using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using KSC.MeetingManagement.Api.Security;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class SecurityTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly Tenant _tenant = new(Guid.NewGuid(), "a", "A", "UTC");
    private readonly Guid _auditor = Guid.NewGuid();
    private readonly Guid _auditor2 = Guid.NewGuid();
    private readonly Guid _participant = Guid.NewGuid();

    private HttpClient Client(Dictionary<string, string?>? settings = null)
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
            });
        });

        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenant);
        var members = app.Services.GetRequiredService<InMemoryMembershipStore>();
        members.Add(new Membership(_tenant.Id, _auditor, [Roles.Auditor]));
        members.Add(new Membership(_tenant.Id, _auditor2, [Roles.Auditor]));
        members.Add(new Membership(_tenant.Id, _participant, [Roles.Participant]));
        return app.CreateClient(new() { AllowAutoRedirect = false });
    }

    private HttpRequestMessage Request(HttpMethod method, string url, Guid? user = null, string? origin = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Tenant-Id", _tenant.Id.ToString());
        if (user is { } u)
        {
            request.Headers.Add("X-Test-User", u.ToString());
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Security_headers_are_on_every_kind_of_response(int expected)
    {
        var client = Client();
        var request = expected switch
        {
            200 => Request(HttpMethod.Get, "/api/v1/reports/actions", _auditor),
            401 => Request(HttpMethod.Get, "/api/v1/reports/actions"),
            403 => Request(HttpMethod.Get, "/api/v1/reports/actions", _participant),
            _ => Request(HttpMethod.Get, $"/api/v1/meetings/{Guid.NewGuid()}", _auditor),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Contains("camera=()", response.Headers.GetValues("Permissions-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Guest_endpoints_are_rate_limited_and_the_refusal_says_when_to_retry()
    {
        var client = Client(new() { ["RateLimit:GuestPerMinute"] = "3" });
        var url = $"/api/v1/meetings/{Guid.NewGuid()}/participants/{Guid.NewGuid()}/redeem";

        var codes = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 5; i++)
        {
            last = await client.SendAsync(Request(HttpMethod.Post, url, body: new { token = "guess" + i }));
            codes.Add(last.StatusCode);
        }

        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], codes);
        Assert.True(last!.Headers.TryGetValues("Retry-After", out var retry) && int.Parse(retry.Single(), System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal("nosniff", last.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(429, (await last.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Sensitive_endpoints_are_limited_per_user()
    {
        var client = Client(new() { ["RateLimit:SensitivePerMinute"] = "2" });

        var first = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            first.Add((await client.SendAsync(Request(HttpMethod.Get, "/api/v1/reports/actions", _auditor))).StatusCode);
        }

        var otherUser = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/reports/actions", _auditor2));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], first);
        Assert.Equal(HttpStatusCode.OK, otherUser.StatusCode);
    }

    [Fact]
    public async Task Without_configuration_no_cross_origin_access_is_granted()
    {
        var client = Client();

        var response = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/reports/actions", _auditor, origin: "https://evil.example"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Only_listed_origins_are_allowed_and_wildcards_are_ignored()
    {
        var client = Client(new() { ["Cors:AllowedOrigins"] = "https://app.example.org, *" });

        var allowed = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/reports/actions", _auditor, origin: "https://app.example.org"));
        var other = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/reports/actions", _auditor, origin: "https://evil.example"));
        var preflightAllowed = await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/api/v1/meetings")
        {
            Headers = { { "Origin", "https://app.example.org" }, { "Access-Control-Request-Method", "POST" } },
        });
        var preflightEvil = await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/api/v1/meetings")
        {
            Headers = { { "Origin", "https://evil.example" }, { "Access-Control-Request-Method", "POST" } },
        });

        Assert.Equal("https://app.example.org", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("https://app.example.org", preflightAllowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(preflightEvil.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("wss://media.example.org", null, null)]
    [InlineData(null, "key", null)]
    [InlineData("wss://media.example.org", "key", "too-short")]
    [InlineData(null, null, "0123456789abcdef0123456789abcdef-secret")]
    public void Half_configured_media_stops_startup(string? url, string? key, string? secret)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Media:ServerUrl"] = url,
            ["Media:ApiKey"] = key,
            ["Media:ApiSecret"] = secret,
        }).Build();

        Assert.Throws<InvalidOperationException>(config.ValidateKscConfiguration);
    }

    [Fact]
    public void Empty_or_complete_media_settings_are_accepted()
    {
        var empty = new ConfigurationBuilder().Build();
        var complete = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Media:ServerUrl"] = "wss://media.example.org",
            ["Media:ApiKey"] = "key",
            ["Media:ApiSecret"] = "0123456789abcdef0123456789abcdef-secret",
        }).Build();

        empty.ValidateKscConfiguration();
        complete.ValidateKscConfiguration();
    }

    [Fact]
    public void An_app_with_incomplete_media_settings_refuses_to_start()
    {
        var broken = factory.WithWebHostBuilder(b => b.UseSetting("Media:ApiKey", "only-a-key"));

        Assert.ThrowsAny<Exception>(() => broken.CreateClient());
    }

    [Fact]
    public void Committed_configuration_files_contain_no_secrets()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && root.GetFiles("KSC.MeetingManagement.sln").Length == 0)
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var files = Directory.GetFiles(root.FullName, "appsettings*.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);

        var sensitiveKey = new Regex("password|secret|apikey|api_key|token|privatekey", RegexOptions.IgnoreCase);
        var offenders = new List<string>();
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            Walk(doc.RootElement, string.Empty);

            void Walk(JsonElement element, string path)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (var property in element.EnumerateObject())
                        {
                            Walk(property.Value, path + "/" + property.Name);
                        }

                        break;
                    case JsonValueKind.String:
                        var value = element.GetString() ?? string.Empty;
                        if (value.Length > 0 && (sensitiveKey.IsMatch(path) || Regex.IsMatch(value, "(password|pwd)=[^;]+", RegexOptions.IgnoreCase)))
                        {
                            offenders.Add($"{Path.GetFileName(file)}{path}");
                        }

                        break;
                }
            }
        }

        Assert.Empty(offenders);
    }
}
