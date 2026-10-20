using System.Net;
using System.Net.Http.Json;
using KSC.MeetingManagement.Api.Controllers;
using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Persistence;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class IdentityAdminApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string SetupToken = "setup-token-for-tests-1234567890";
    private const string Password = "Correct-horse-1";
    private const string Key = "unit-test-signing-key-that-is-long-enough-123";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly Tenant _tenant = new(Guid.NewGuid(), "acme", "Acme", "UTC");
    private readonly Tenant _other = new(Guid.NewGuid(), "other", "Other", "UTC");
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _participant = Guid.NewGuid();
    private readonly Guid _stranger = Guid.NewGuid();

    public IdentityAdminApiTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private WebApplicationFactory<Program> Build(bool seed, bool setupToken = true)
    {
        var app = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:SigningKey", Key);
            if (setupToken)
            {
                b.UseSetting("Setup:Token", SetupToken);
            }

            b.ConfigureServices(s =>
            {
                s.UseInMemoryKscDatabase(Guid.NewGuid().ToString());
                s.RemoveAll<IPasswordHasher>();
                s.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(1000));
                s.AddAuthentication(TestAuthHandler.Scheme)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
            });
        });
        if (seed)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KscDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Tenants.AddRange(_tenant, _other);
            void Add(Tenant t, Guid id, string email, string role)
            {
                db.Users.Add(new User(id, email, email, hasher.Hash(Password), DateTimeOffset.UtcNow));
                db.Memberships.Add(MembershipRow.From(new Membership(t.Id, id, [role])));
            }

            Add(_tenant, _owner, "owner@example.com", Roles.TenantOwner);
            Add(_tenant, _admin, "admin@example.com", Roles.TenantAdministrator);
            Add(_tenant, _participant, "part@example.com", Roles.Participant);
            Add(_other, _stranger, "stranger@example.com", Roles.TenantOwner);
            db.SaveChanges();
        }

        return app;
    }

    private HttpRequestMessage Req(HttpMethod method, string url, Guid? caller, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Tenant-Id", _tenant.Id.ToString());
        if (caller is { } id)
        {
            request.Headers.Add("X-Test-User", id.ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static SetupRequest ValidSetup(string password = Password) =>
        new("Acme Corp", "acme-corp", "Asia/Tehran", "boss@example.com", "Boss", password);

    private static HttpRequestMessage SetupReq(SetupRequest body, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/setup") { Content = JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Add("X-Setup-Token", token);
        }

        return request;
    }

    [Fact]
    public async Task Setup_creates_the_first_organisation_and_owner_who_can_then_sign_in_once()
    {
        var client = Build(false).CreateClient();

        var created = await client.SendAsync(SetupReq(ValidSetup(), SetupToken));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("boss@example.com", Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var profile = (await login.Content.ReadFromJsonAsync<AuthResponse>())!.User;
        Assert.Contains(Roles.TenantOwner, Assert.Single(profile.Memberships).Roles);

        var again = await client.SendAsync(SetupReq(ValidSetup(), SetupToken));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Setup_is_hidden_without_a_configured_token_and_rejects_wrong_or_missing_tokens()
    {
        var hidden = Build(false, setupToken: false).CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await hidden.SendAsync(SetupReq(ValidSetup(), SetupToken))).StatusCode);

        var client = Build(false).CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(SetupReq(ValidSetup(), "wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(SetupReq(ValidSetup(), null))).StatusCode);
    }

    [Fact]
    public async Task Setup_rejects_weak_passwords_bad_emails_and_unknown_time_zones()
    {
        var client = Build(false).CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(SetupReq(ValidSetup("short"), SetupToken))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(SetupReq(ValidSetup() with { Email = "nope" }, SetupToken))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(SetupReq(ValidSetup() with { TimeZoneId = "Mars/Base" }, SetupToken))).StatusCode);
    }

    [Fact]
    public async Task An_administrator_adds_a_new_member_who_can_sign_in_and_appears_in_the_list()
    {
        var client = Build(true).CreateClient();

        var add = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/users", _admin, new AddMemberRequest("New@Example.com", "New", Password, [Roles.Secretary])));
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("new@example.com", Password))).StatusCode);

        var list = (await (await client.SendAsync(Req(HttpMethod.Get, "/api/v1/users", _admin))).Content.ReadFromJsonAsync<List<MemberDto>>())!;
        Assert.Contains(list, m => m.Email == "new@example.com" && m.Roles.Contains(Roles.Secretary));
        Assert.DoesNotContain(list, m => m.Email == "stranger@example.com");
    }

    [Fact]
    public async Task Adding_an_existing_account_only_adds_a_membership_and_a_duplicate_is_a_conflict()
    {
        var client = Build(true).CreateClient();
        var body = new AddMemberRequest("stranger@example.com", null, null, [Roles.Participant]);

        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(Req(HttpMethod.Post, "/api/v1/users", _admin, body))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Req(HttpMethod.Post, "/api/v1/users", _admin, body))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("stranger@example.com", Password))).StatusCode);
    }

    [Fact]
    public async Task Invalid_input_is_rejected()
    {
        var client = Build(true).CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Req(HttpMethod.Post, "/api/v1/users", _admin, new AddMemberRequest("a@b.com", "A", Password, ["Wizard"])))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Req(HttpMethod.Post, "/api/v1/users", _admin, new AddMemberRequest("a@b.com", "A", Password, [])))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Req(HttpMethod.Post, "/api/v1/users", _admin, new AddMemberRequest("a@b.com", "A", "weak", [Roles.Participant])))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Req(HttpMethod.Post, "/api/v1/users", _admin, new AddMemberRequest("not-mail", "A", Password, [Roles.Participant])))).StatusCode);
    }

    [Fact]
    public async Task Members_without_the_users_permission_and_anonymous_callers_are_refused()
    {
        var client = Build(true).CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Req(HttpMethod.Get, "/api/v1/users", _participant))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Req(HttpMethod.Get, "/api/v1/users", null))).StatusCode);
    }

    [Fact]
    public async Task Only_an_owner_can_grant_or_change_the_owner_role()
    {
        var client = Build(true).CreateClient();

        var byAdmin = await client.SendAsync(Req(HttpMethod.Put, $"/api/v1/users/{_participant}/roles", _admin, new SetRolesRequest([Roles.TenantOwner])));
        Assert.Equal(HttpStatusCode.Forbidden, byAdmin.StatusCode);

        var addOwnerByAdmin = await client.SendAsync(Req(HttpMethod.Post, "/api/v1/users", _admin, new AddMemberRequest("o2@example.com", "O", Password, [Roles.TenantOwner])));
        Assert.Equal(HttpStatusCode.Forbidden, addOwnerByAdmin.StatusCode);

        var disableOwner = await client.SendAsync(Req(HttpMethod.Put, $"/api/v1/users/{_owner}/active", _admin, new SetActiveRequest(false)));
        Assert.Equal(HttpStatusCode.Forbidden, disableOwner.StatusCode);

        var byOwner = await client.SendAsync(Req(HttpMethod.Put, $"/api/v1/users/{_participant}/roles", _owner, new SetRolesRequest([Roles.TenantOwner, Roles.Participant])));
        Assert.Equal(HttpStatusCode.OK, byOwner.StatusCode);
        Assert.Contains(Roles.TenantOwner, (await byOwner.Content.ReadFromJsonAsync<MemberDto>())!.Roles);
    }

    [Fact]
    public async Task Changing_roles_replaces_them_and_nobody_can_change_their_own_membership()
    {
        var client = Build(true).CreateClient();

        var changed = await client.SendAsync(Req(HttpMethod.Put, $"/api/v1/users/{_participant}/roles", _admin, new SetRolesRequest([Roles.Approver])));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal([Roles.Approver], (await changed.Content.ReadFromJsonAsync<MemberDto>())!.Roles);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Req(HttpMethod.Put, $"/api/v1/users/{_admin}/roles", _admin, new SetRolesRequest([Roles.Participant])))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Req(HttpMethod.Put, $"/api/v1/users/{_admin}/active", _admin, new SetActiveRequest(false)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Req(HttpMethod.Put, $"/api/v1/users/{_stranger}/roles", _admin, new SetRolesRequest([Roles.Approver])))).StatusCode);
    }

    [Fact]
    public async Task A_deactivated_member_loses_access_to_the_organisation()
    {
        var client = Build(true).CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Req(HttpMethod.Get, "/api/v1/committees", _participant))).StatusCode);

        var off = await client.SendAsync(Req(HttpMethod.Put, $"/api/v1/users/{_participant}/active", _admin, new SetActiveRequest(false)));
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Req(HttpMethod.Get, "/api/v1/committees", _participant))).StatusCode);

        await client.SendAsync(Req(HttpMethod.Put, $"/api/v1/users/{_participant}/active", _admin, new SetActiveRequest(true)));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Req(HttpMethod.Get, "/api/v1/committees", _participant))).StatusCode);
    }
}
