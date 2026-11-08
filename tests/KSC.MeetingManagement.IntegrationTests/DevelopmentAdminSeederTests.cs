using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KSC.MeetingManagement.Api;
using KSC.MeetingManagement.Infrastructure.Persistence;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class DevelopmentAdminSeederTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Key = "development-seed-test-signing-key-long-enough-123";
    private readonly WebApplicationFactory<Program> _factory;

    public DevelopmentAdminSeederTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Enabled_development_seed_creates_owner_that_can_sign_in()
    {
        using var app = Build();
        await DevelopmentAdminSeeder.SeedIfEnabledAsync(app.Services, isDevelopment: true, enabled: true);

        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = DevelopmentAdminSeeder.Email,
            password = DevelopmentAdminSeeder.Password,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(DevelopmentAdminSeeder.Email, body.GetProperty("user").GetProperty("email").GetString());
        Assert.Contains("TenantOwner", body.GetProperty("user").GetProperty("memberships")[0].GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Disabled_or_nondevelopment_seed_does_not_create_user(bool isDevelopment, bool enabled)
    {
        using var app = Build();
        await DevelopmentAdminSeeder.SeedIfEnabledAsync(app.Services, isDevelopment, enabled);
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = DevelopmentAdminSeeder.Email,
            password = DevelopmentAdminSeeder.Password,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Existing_database_is_not_modified()
    {
        using var app = Build();
        var admin = app.Services.CreateScope().ServiceProvider.GetRequiredService<KSC.MeetingManagement.Application.Identity.IdentityAdminService>();
        var setup = await admin.SetupAsync(new(
            "Existing", "existing", "UTC", "existing@example.com", "Existing", "Existing-password-123"), CancellationToken.None);
        Assert.True(setup.Succeeded);

        await DevelopmentAdminSeeder.SeedIfEnabledAsync(app.Services, isDevelopment: true, enabled: true);

        using var client = app.CreateClient();
        var seededLogin = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = DevelopmentAdminSeeder.Email,
            password = DevelopmentAdminSeeder.Password,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, seededLogin.StatusCode);
    }

    private WebApplicationFactory<Program> Build() => _factory.WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Auth:SigningKey", Key);
        builder.UseSetting("DevelopmentSeed:Enabled", "false");
        builder.ConfigureServices(services =>
        {
            services.UseInMemoryKscDatabase(Guid.NewGuid().ToString());
            services.RemoveAll<KSC.MeetingManagement.Application.Identity.IPasswordHasher>();
            services.AddSingleton<KSC.MeetingManagement.Application.Identity.IPasswordHasher>(new Pbkdf2PasswordHasher(1000));
        });
    });
}
