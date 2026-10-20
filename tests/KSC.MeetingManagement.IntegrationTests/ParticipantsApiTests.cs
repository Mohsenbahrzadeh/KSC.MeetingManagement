using System.Net;
using System.Net.Http.Json;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Participants;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class ParticipantsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Tenant _tenantA = new(Guid.NewGuid(), "a", "A", "Asia/Tehran");
    private readonly Tenant _tenantB = new(Guid.NewGuid(), "b", "B", "Asia/Tehran");
    private readonly Guid _organizerA = Guid.NewGuid();
    private readonly Guid _organizerB = Guid.NewGuid();
    private readonly Guid _memberA = Guid.NewGuid();
    private readonly Guid _outsider = Guid.NewGuid();
    private readonly HttpClient _client;

    public ParticipantsApiTests(WebApplicationFactory<Program> factory)
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
        members.Add(new Membership(_tenantA.Id, _organizerA, [Roles.MeetingOrganizer]));
        members.Add(new Membership(_tenantA.Id, _memberA, [Roles.Participant]));
        members.Add(new Membership(_tenantB.Id, _organizerB, [Roles.MeetingOrganizer]));

        _client = app.CreateClient(new() { AllowAutoRedirect = false });
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, Tenant tenant, Guid? user, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Tenant-Id", tenant.Id.ToString());
        if (user is { } u)
        {
            request.Headers.Add("X-Test-User", u.ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _client.SendAsync(request);
    }

    private async Task<Guid> CreateMeeting()
    {
        var response = await Send(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizerA, new { title = "Board", mode = "Online" });
        return (await response.Content.ReadFromJsonAsync<MeetingDto>(Json))!.Id;
    }

    private async Task<GuestInvitationDto> InviteGuest(Guid meetingId)
    {
        var response = await Send(HttpMethod.Post, $"/api/v1/meetings/{meetingId}/participants/guests", _tenantA, _organizerA,
            new { name = "Guest", email = "guest@example.com", expiresUtc = DateTimeOffset.UtcNow.AddDays(2) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GuestInvitationDto>(Json))!;
    }

    [Fact]
    public async Task Organizer_adds_internal_member_once()
    {
        var meetingId = await CreateMeeting();
        var url = $"/api/v1/meetings/{meetingId}/participants/internal";

        var first = await Send(HttpMethod.Post, url, _tenantA, _organizerA, new { userId = _memberA });
        var duplicate = await Send(HttpMethod.Post, url, _tenantA, _organizerA, new { userId = _memberA });
        var stranger = await Send(HttpMethod.Post, url, _tenantA, _organizerA, new { userId = _outsider });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stranger.StatusCode);
    }

    [Fact]
    public async Task Guest_redeems_with_valid_token_and_list_never_leaks_it()
    {
        var meetingId = await CreateMeeting();
        var invite = await InviteGuest(meetingId);
        var baseUrl = $"/api/v1/meetings/{meetingId}/participants";

        var redeem = await Send(HttpMethod.Post, $"{baseUrl}/{invite.Participant.Id}/redeem", _tenantA, null, new { token = invite.Token });
        Assert.Equal(HttpStatusCode.OK, redeem.StatusCode);

        var list = await Send(HttpMethod.Get, baseUrl, _tenantA, _organizerA);
        var body = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain(invite.Token, body, StringComparison.Ordinal);
        Assert.DoesNotContain("tokenHash", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_token_and_revoked_invitation_do_not_grant_access()
    {
        var meetingId = await CreateMeeting();
        var invite = await InviteGuest(meetingId);
        var redeemUrl = $"/api/v1/meetings/{meetingId}/participants/{invite.Participant.Id}/redeem";

        var wrong = await Send(HttpMethod.Post, redeemUrl, _tenantA, null, new { token = "nope" });
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);

        var revoke = await Send(HttpMethod.Post, $"/api/v1/meetings/{meetingId}/participants/{invite.Participant.Id}/revoke", _tenantA, _organizerA);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var afterRevoke = await Send(HttpMethod.Post, redeemUrl, _tenantA, null, new { token = invite.Token });
        Assert.Equal(HttpStatusCode.NotFound, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task Token_does_not_work_in_another_tenant()
    {
        var meetingId = await CreateMeeting();
        var invite = await InviteGuest(meetingId);

        var response = await Send(HttpMethod.Post,
            $"/api/v1/meetings/{meetingId}/participants/{invite.Participant.Id}/redeem", _tenantB, null, new { token = invite.Token });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Invitation_lifetime_is_capped_and_past_expiry_rejected()
    {
        var meetingId = await CreateMeeting();
        var url = $"/api/v1/meetings/{meetingId}/participants/guests";

        var tooLong = await Send(HttpMethod.Post, url, _tenantA, _organizerA,
            new { name = "G", email = "g@example.com", expiresUtc = DateTimeOffset.UtcNow.AddDays(400) });
        var past = await Send(HttpMethod.Post, url, _tenantA, _organizerA,
            new { name = "G", email = "g@example.com", expiresUtc = DateTimeOffset.UtcNow.AddDays(-1) });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, past.StatusCode);
    }

    [Fact]
    public async Task Participant_role_cannot_invite_and_other_tenant_gets_not_found()
    {
        var meetingId = await CreateMeeting();
        var body = new { name = "G", email = "g@example.com", expiresUtc = DateTimeOffset.UtcNow.AddDays(1) };

        var forbidden = await Send(HttpMethod.Post, $"/api/v1/meetings/{meetingId}/participants/guests", _tenantA, _memberA, body);
        var otherTenant = await Send(HttpMethod.Post, $"/api/v1/meetings/{meetingId}/participants/guests", _tenantB, _organizerB, body);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherTenant.StatusCode);
    }
}
