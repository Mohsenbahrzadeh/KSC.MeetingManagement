using System.Net;
using System.Net.Http.Json;
using KSC.MeetingManagement.Application.Committees;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class CommitteesApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Tenant _tenantA = new(Guid.NewGuid(), "a", "A", "Asia/Tehran");
    private readonly Tenant _tenantB = new(Guid.NewGuid(), "b", "B", "Asia/Tehran");
    private readonly Guid _adminA = Guid.NewGuid();
    private readonly Guid _organizerA = Guid.NewGuid();
    private readonly Guid _memberA = Guid.NewGuid();
    private readonly Guid _member2A = Guid.NewGuid();
    private readonly Guid _adminB = Guid.NewGuid();
    private readonly Guid _outsider = Guid.NewGuid();
    private readonly HttpClient _client;

    public CommitteesApiTests(WebApplicationFactory<Program> factory)
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(s =>
            {
                s.UseInMemoryKscDatabase(Guid.NewGuid().ToString());
                s.AddAuthentication(TestAuthHandler.Scheme)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
            });
        });

        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenantA);
        app.Services.GetRequiredService<InMemoryTenantStore>().Add(_tenantB);
        var members = app.Services.GetRequiredService<InMemoryMembershipStore>();
        members.Add(new Membership(_tenantA.Id, _adminA, [Roles.TenantAdministrator]));
        members.Add(new Membership(_tenantA.Id, _organizerA, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenantA.Id, _memberA, [Roles.Participant]));
        members.Add(new Membership(_tenantA.Id, _member2A, [Roles.Participant]));
        members.Add(new Membership(_tenantB.Id, _adminB, [Roles.TenantAdministrator]));

        _client = app.CreateClient(new() { AllowAutoRedirect = false });
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, Tenant tenant, Guid user, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Tenant-Id", tenant.Id.ToString());
        request.Headers.Add("X-Test-User", user.ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _client.SendAsync(request);
    }

    private async Task<CommitteeDto> CreateCommittee()
    {
        var response = await Send(HttpMethod.Post, "/api/v1/committees", _tenantA, _adminA, new { name = "Audit", description = "d" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CommitteeDto>(Json))!;
    }

    [Fact]
    public async Task Admin_creates_updates_and_everyone_can_read()
    {
        var c = await CreateCommittee();

        var update = await Send(HttpMethod.Put, $"/api/v1/committees/{c.Id}", _tenantA, _adminA, new { name = "Audit 2", isActive = false });
        var read = await Send(HttpMethod.Get, $"/api/v1/committees/{c.Id}", _tenantA, _memberA);
        var dto = await read.Content.ReadFromJsonAsync<CommitteeDto>(Json);

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("Audit 2", dto!.Name);
        Assert.False(dto.IsActive);
    }

    [Fact]
    public async Task Only_organization_managers_can_modify()
    {
        var c = await CreateCommittee();

        var create = await Send(HttpMethod.Post, "/api/v1/committees", _tenantA, _organizerA, new { name = "X" });
        var addMember = await Send(HttpMethod.Post, $"/api/v1/committees/{c.Id}/members", _tenantA, _memberA, new { userId = _memberA, role = "Member" });

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, addMember.StatusCode);
    }

    [Fact]
    public async Task Member_management_enforces_rules()
    {
        var c = await CreateCommittee();
        var url = $"/api/v1/committees/{c.Id}/members";

        var chair = await Send(HttpMethod.Post, url, _tenantA, _adminA, new { userId = _memberA, role = "Chair" });
        var secondChair = await Send(HttpMethod.Post, url, _tenantA, _adminA, new { userId = _member2A, role = "Chair" });
        var duplicate = await Send(HttpMethod.Post, url, _tenantA, _adminA, new { userId = _memberA, role = "Member" });
        var stranger = await Send(HttpMethod.Post, url, _tenantA, _adminA, new { userId = _outsider, role = "Member" });

        Assert.Equal(HttpStatusCode.OK, chair.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, secondChair.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stranger.StatusCode);
    }

    [Fact]
    public async Task Role_change_and_removal_work_and_unknown_member_is_not_found()
    {
        var c = await CreateCommittee();
        await Send(HttpMethod.Post, $"/api/v1/committees/{c.Id}/members", _tenantA, _adminA, new { userId = _memberA, role = "Member" });

        var change = await Send(HttpMethod.Put, $"/api/v1/committees/{c.Id}/members/{_memberA}", _tenantA, _adminA, new { role = "Secretary" });
        var changed = await change.Content.ReadFromJsonAsync<CommitteeDto>(Json);
        var remove = await Send(HttpMethod.Delete, $"/api/v1/committees/{c.Id}/members/{_memberA}", _tenantA, _adminA);
        var removeAgain = await Send(HttpMethod.Delete, $"/api/v1/committees/{c.Id}/members/{_memberA}", _tenantA, _adminA);

        Assert.Equal("Secretary", changed!.Members.Single().Role.ToString());
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, removeAgain.StatusCode);
    }

    [Fact]
    public async Task Committees_are_isolated_between_tenants()
    {
        var c = await CreateCommittee();

        var read = await Send(HttpMethod.Get, $"/api/v1/committees/{c.Id}", _tenantB, _adminB);
        var update = await Send(HttpMethod.Put, $"/api/v1/committees/{c.Id}", _tenantB, _adminB, new { name = "Hijack", isActive = true });
        var list = await (await Send(HttpMethod.Get, "/api/v1/committees", _tenantB, _adminB)).Content.ReadFromJsonAsync<List<CommitteeDto>>(Json);

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Empty(list!);
    }

    [Fact]
    public async Task Blank_name_is_rejected()
    {
        var response = await Send(HttpMethod.Post, "/api/v1/committees", _tenantA, _adminA, new { name = " " });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}
