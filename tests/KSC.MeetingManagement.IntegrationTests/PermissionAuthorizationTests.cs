using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KSC.MeetingManagement.IntegrationTests;

/// <summary>Test-only scheme: the caller identity comes from the X-Test-User header.</summary>
internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public new const string Scheme = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var user) || string.IsNullOrEmpty(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], Scheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public sealed class PermissionAuthorizationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly Tenant _tenantA = new(Guid.NewGuid(), "a", "A", "Asia/Tehran");
    private readonly Tenant _tenantB = new(Guid.NewGuid(), "b", "B", "Asia/Tehran");
    private readonly Guid _organizerInA = Guid.NewGuid();
    private readonly Guid _participantInA = Guid.NewGuid();
    private readonly Guid _disabledInA = Guid.NewGuid();
    private readonly HttpClient _client;

    public PermissionAuthorizationTests(WebApplicationFactory<Program> factory)
    {
        var app = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.UseInMemoryKscDatabase(Guid.NewGuid().ToString());
            services.AddAuthentication(TestAuthHandler.Scheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
        }));

        var tenants = app.Services.GetRequiredService<InMemoryTenantStore>();
        tenants.Add(_tenantA);
        tenants.Add(_tenantB);

        var members = app.Services.GetRequiredService<InMemoryMembershipStore>();
        members.Add(new Membership(_tenantA.Id, _organizerInA, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenantA.Id, _participantInA, [Roles.Participant]));
        var disabled = new Membership(_tenantA.Id, _disabledInA, [Roles.TenantOwner]);
        disabled.Disable();
        members.Add(disabled);

        _client = app.CreateClient(new() { AllowAutoRedirect = false });
    }

    private Task<HttpResponseMessage> Call(Guid tenantId, Guid? userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ping/protected");
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        if (userId is not null)
        {
            request.Headers.Add("X-Test-User", userId.Value.ToString());
        }

        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Organizer_is_allowed() =>
        Assert.Equal(HttpStatusCode.OK, (await Call(_tenantA.Id, _organizerInA)).StatusCode);

    [Fact]
    public async Task Participant_without_permission_is_forbidden() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(_tenantA.Id, _participantInA)).StatusCode);

    [Fact]
    public async Task Anonymous_caller_is_not_authorized() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(_tenantA.Id, null)).StatusCode);

    [Fact]
    public async Task Member_of_tenant_A_cannot_act_in_tenant_B() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(_tenantB.Id, _organizerInA)).StatusCode);

    [Fact]
    public async Task Disabled_membership_is_forbidden() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(_tenantA.Id, _disabledInA)).StatusCode);

    [Fact]
    public async Task Unknown_user_is_forbidden() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(_tenantA.Id, Guid.NewGuid())).StatusCode);
}
