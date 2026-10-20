using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KSC.MeetingManagement.Application.Attachments;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class AttachmentsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x0A];

    private readonly Tenant _tenantA = new(Guid.NewGuid(), "a", "A", "Asia/Tehran");
    private readonly Tenant _tenantB = new(Guid.NewGuid(), "b", "B", "Asia/Tehran");
    private readonly Guid _organizerA = Guid.NewGuid();
    private readonly Guid _participantA = Guid.NewGuid();
    private readonly Guid _organizerB = Guid.NewGuid();
    private readonly HttpClient _client;
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), "ksc-test-" + Guid.NewGuid().ToString("N"));

    public AttachmentsApiTests(WebApplicationFactory<Program> factory)
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Storage:Root", _storageRoot);
            b.UseSetting("Attachments:MaxBytes", "1024");
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
        members.Add(new Membership(_tenantA.Id, _participantA, [Roles.Participant]));
        members.Add(new Membership(_tenantB.Id, _organizerB, [Roles.MeetingOrganizer]));

        _client = app.CreateClient(new() { AllowAutoRedirect = false });
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, Tenant tenant, Guid user)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Tenant-Id", tenant.Id.ToString());
        request.Headers.Add("X-Test-User", user.ToString());
        return request;
    }

    private async Task<Guid> CreateMeeting()
    {
        var request = Request(HttpMethod.Post, "/api/v1/meetings", _tenantA, _organizerA);
        request.Content = JsonContent.Create(new { title = "Board", mode = "Online" });
        var response = await _client.SendAsync(request);
        return (await response.Content.ReadFromJsonAsync<MeetingDto>(Json))!.Id;
    }

    private Task<HttpResponseMessage> Upload(Guid meetingId, string fileName, byte[] bytes, Tenant tenant, Guid user)
    {
        var request = Request(HttpMethod.Post, $"/api/v1/meetings/{meetingId}/attachments", tenant, user);
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);
        request.Content = content;
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Get(string url, Tenant tenant, Guid user) =>
        _client.SendAsync(Request(HttpMethod.Get, url, tenant, user));

    [Fact]
    public async Task Organizer_uploads_and_members_download_same_bytes()
    {
        var meetingId = await CreateMeeting();

        var upload = await Upload(meetingId, "agenda.pdf", Pdf, _tenantA, _organizerA);
        var dto = await upload.Content.ReadFromJsonAsync<AttachmentDto>(Json);
        var download = await Get($"/api/v1/meetings/{meetingId}/attachments/{dto!.Id}", _tenantA, _participantA);

        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Pdf, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Download_is_denied_without_permission_and_across_tenants()
    {
        var meetingId = await CreateMeeting();
        var dto = await (await Upload(meetingId, "a.pdf", Pdf, _tenantA, _organizerA)).Content.ReadFromJsonAsync<AttachmentDto>(Json);
        var url = $"/api/v1/meetings/{meetingId}/attachments/{dto!.Id}";

        var stranger = await Get(url, _tenantA, Guid.NewGuid());
        var otherTenant = await Get(url, _tenantB, _organizerB);

        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherTenant.StatusCode);
    }

    [Fact]
    public async Task Participant_cannot_upload()
    {
        var meetingId = await CreateMeeting();

        var response = await Upload(meetingId, "a.pdf", Pdf, _tenantA, _participantA);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("run.exe")]
    [InlineData("page.html")]
    [InlineData("noextension")]
    public async Task Disallowed_types_are_rejected(string fileName)
    {
        var meetingId = await CreateMeeting();

        var response = await Upload(meetingId, fileName, Pdf, _tenantA, _organizerA);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Content_must_match_extension()
    {
        var meetingId = await CreateMeeting();

        var response = await Upload(meetingId, "fake.pdf", "MZ-not-a-pdf"u8.ToArray(), _tenantA, _organizerA);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_file_is_rejected()
    {
        var meetingId = await CreateMeeting();
        var big = new byte[2048];
        Pdf.CopyTo(big, 0);

        var response = await Upload(meetingId, "big.pdf", big, _tenantA, _organizerA);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Path_in_file_name_is_stripped_and_delete_removes_file()
    {
        var meetingId = await CreateMeeting();
        var dto = await (await Upload(meetingId, @"..\..\evil\notes.txt", "hello"u8.ToArray(), _tenantA, _organizerA))
            .Content.ReadFromJsonAsync<AttachmentDto>(Json);
        Assert.Equal("notes.txt", dto!.FileName);

        var url = $"/api/v1/meetings/{meetingId}/attachments/{dto.Id}";
        var delete = await _client.SendAsync(Request(HttpMethod.Delete, url, _tenantA, _organizerA));
        var after = await Get(url, _tenantA, _organizerA);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }
}
