using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KSC.MeetingManagement.Api.Controllers;
using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Persistence;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class AuthApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Password = "Correct-horse-1";
    private const string Key = "unit-test-signing-key-that-is-long-enough-123";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly Tenant _tenant = new(Guid.NewGuid(), "acme", "Acme", "UTC");
    private readonly Guid _userId = Guid.NewGuid();

    public AuthApiTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private WebApplicationFactory<Program> Build(bool configured)
    {
        var app = _factory.WithWebHostBuilder(b =>
        {
            if (configured)
            {
                b.UseSetting("Auth:SigningKey", Key);
            }

            b.ConfigureServices(s =>
            {
                s.UseInMemoryKscDatabase(Guid.NewGuid().ToString());
                s.RemoveAll<IPasswordHasher>();
                s.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(1000));
            });
        });
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KscDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        db.Tenants.Add(_tenant);
        db.Memberships.Add(MembershipRow.From(new Membership(_tenant.Id, _userId, [Roles.MeetingOrganizer, Roles.Participant])));
        db.Users.Add(new User(_userId, "Ali@Example.com", "Ali", hasher.Hash(Password), DateTimeOffset.UtcNow));
        db.SaveChanges();
        return app;
    }

    private static async Task<string?> TitleOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("title").GetString();

    private static async Task<AuthResponse> LoginOk(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(" ali@example.com ", Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    [Fact]
    public async Task Login_returns_tokens_and_the_profile_lists_active_memberships_with_roles()
    {
        var client = Build(true).CreateClient();

        var auth = await LoginOk(client);
        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var response = await client.SendAsync(me);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = (await response.Content.ReadFromJsonAsync<UserProfile>())!;
        Assert.Equal("ali@example.com", profile.Email);
        var membership = Assert.Single(profile.Memberships);
        Assert.Equal(_tenant.Id, membership.TenantId);
        Assert.Contains(Roles.MeetingOrganizer, membership.Roles);
    }

    [Fact]
    public async Task The_access_token_authorises_tenant_endpoints_through_the_database_membership()
    {
        var client = Build(true).CreateClient();
        var auth = await LoginOk(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/committees");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.Add("X-Tenant-Id", _tenant.Id.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);

        using var otherTenant = new HttpRequestMessage(HttpMethod.Get, "/api/v1/committees");
        otherTenant.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        otherTenant.Headers.Add("X-Tenant-Id", Guid.NewGuid().ToString());
        Assert.True((await client.SendAsync(otherTenant)).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_give_the_same_answer()
    {
        var client = Build(true).CreateClient();

        var wrong = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("ali@example.com", "nope"));
        var unknown = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nobody@example.com", "nope"));
        var malformed = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("not-an-email", "nope"));

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, malformed.StatusCode);
        Assert.Equal(await TitleOf(wrong), await TitleOf(unknown));
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_even_for_the_right_password()
    {
        var client = Build(true).CreateClient();

        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("ali@example.com", "bad"));
        }

        var locked = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("ali@example.com", Password));
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_reusing_an_old_one_revokes_everything()
    {
        var client = Build(true).CreateClient();
        var first = await LoginOk(client);

        var refreshed = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var second = (await refreshed.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        var reuse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);

        var afterTheft = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(second.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, afterTheft.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var client = Build(true).CreateClient();
        var auth = await LoginOk(client);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/logout", new RefreshRequest(auth.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task A_deactivated_user_cannot_sign_in()
    {
        var app = Build(true);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KscDbContext>();
            db.Users.Single().Deactivate();
            db.SaveChanges();
        }

        var response = await app.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("ali@example.com", Password));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Without_configuration_login_is_unavailable_and_no_token_is_accepted()
    {
        var client = Build(false).CreateClient();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("ali@example.com", Password))).StatusCode);

        using var forged = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        forged.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.x");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(forged)).StatusCode);
    }

    [Fact]
    public async Task A_tampered_token_is_rejected()
    {
        var client = Build(true).CreateClient();
        var auth = await LoginOk(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken[..^3] + "abc");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }
}
