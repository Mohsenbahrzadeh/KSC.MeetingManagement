using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using KSC.MeetingManagement.Application.Attachments;
using KSC.MeetingManagement.Web.Components.Pages;
using KSC.MeetingManagement.Web.Services;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KSC.MeetingManagement.UnitTests;

public sealed class AttachmentsWebTests : BunitContext, IAsyncLifetime
{
    private static readonly Guid MeetingId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AttachmentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TenantId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly List<(HttpMethod Method, string Path, string? MediaType, string Body)> _requests = [];
    private string[] _roles = ["MeetingOrganizer"];
    private bool _empty;
    private bool _forbidden;
    private bool _largeDownload;
    private IRenderedComponent<MudDialogProvider>? _dialogs;

    public AttachmentsWebTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(new FakeFactory(new Handler(this)));
        Services.AddScoped<AttachmentsApiClient>();
        Services.AddScoped<AuthApiClient>();
        Services.AddSingleton<ISessionStore>(new MemoryStore());
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        Services.AddScoped<SessionState>();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private sealed class Handler(AttachmentsWebTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            owner._requests.Add((request.Method, path, request.Content?.Headers.ContentType?.MediaType, body));
            if (path.EndsWith("/login", StringComparison.Ordinal))
                return Json(JsonSerializer.Serialize(new
                {
                    accessToken = "token",
                    accessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
                    refreshToken = "refresh",
                    refreshTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
                    user = new
                    {
                        id = Guid.NewGuid(),
                        email = "user@example.com",
                        displayName = "User",
                        memberships = new[] { new { tenantId = TenantId, tenantName = "Tenant", tenantSlug = "tenant", roles = owner._roles } },
                    },
                }));
            if (owner._forbidden) return new HttpResponseMessage(HttpStatusCode.Forbidden);
            if (request.Method == HttpMethod.Delete)
            {
                owner._empty = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Post) return Json(Item(), HttpStatusCode.Created);
            if (path.EndsWith($"/{AttachmentId}", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(owner._largeDownload
                        ? new byte[AttachmentOptions.DefaultMaxBytes + 1]
                        : "file"u8.ToArray()),
                };
            return Json(owner._empty ? "[]" : $"[{Item()}]");
        }
    }

    private static string Item() => JsonSerializer.Serialize(new
    {
        id = AttachmentId,
        meetingId = MeetingId,
        fileName = "برنامه.pdf",
        contentType = "application/pdf",
        sizeBytes = 1024,
        sha256 = "ABC",
        uploadedAtUtc = DateTimeOffset.UtcNow,
    });

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private async Task<IRenderedComponent<AttachmentsPage>> RenderPageAsync()
    {
        await Services.GetRequiredService<SessionState>().LoginAsync("user@example.com", "password", CancellationToken.None);
        Render<MudPopoverProvider>();
        _dialogs = Render<MudDialogProvider>();
        return Render<AttachmentsPage>(p => p.Add(c => c.MeetingId, MeetingId));
    }

    [Fact]
    public async Task View_only_user_can_download_but_cannot_upload_or_delete()
    {
        _roles = ["Participant"];
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("برنامه.pdf", page.Markup));
        Assert.DoesNotContain("attachment-file", page.Markup);
        Assert.DoesNotContain("حذف برنامه.pdf", page.Markup);
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.GetAttribute("aria-label") == "دریافت برنامه.pdf").Click());
        page.WaitForAssertion(() => Assert.Contains(_requests, r => r.Path.EndsWith($"/{AttachmentId}", StringComparison.Ordinal)));
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Delete_needs_confirmation_and_refreshes_list()
    {
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("حذف برنامه.pdf", page.Markup));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.GetAttribute("aria-label") == "حذف برنامه.pdf").Click());
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Delete);
        var dialogs = _dialogs!;
        dialogs.WaitForAssertion(() => Assert.Contains("حذف شود؟", dialogs.Markup));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "حذف").Click());
        page.WaitForAssertion(() => Assert.Contains("پیوستی برای این جلسه ثبت نشده است", page.Markup));
        Assert.Contains(_requests, r => r.Method == HttpMethod.Delete && r.Path == $"/api/v1/meetings/{MeetingId}/attachments/{AttachmentId}");
    }

    [Fact]
    public async Task Empty_page_allows_editor_to_select_file()
    {
        _empty = true;
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("پیوستی برای این جلسه ثبت نشده است", page.Markup));
        Assert.Contains("attachment-file", page.Markup);
    }

    [Fact]
    public async Task Forbidden_list_displays_error_without_file_rows()
    {
        _forbidden = true;
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("شما دسترسی لازم", page.Markup));
        Assert.DoesNotContain("دریافت برنامه.pdf", page.Markup);
    }

    [Fact]
    public async Task Client_posts_multipart_file_under_meeting_and_enforces_size_limit()
    {
        var client = Services.GetRequiredService<AttachmentsApiClient>();
        var file = new FakeBrowserFile("test.txt", "hello"u8.ToArray());
        var result = await client.UploadAsync(MeetingId, file, CancellationToken.None);
        Assert.True(result.Succeeded);
        var request = Assert.Single(_requests);
        Assert.Equal($"/api/v1/meetings/{MeetingId}/attachments", request.Path);
        Assert.Equal("multipart/form-data", request.MediaType);
        Assert.Contains("name=file", request.Body);
        Assert.Contains("filename=test.txt", request.Body);
        Assert.Contains("hello", request.Body);
        Assert.Equal(AttachmentOptions.DefaultMaxBytes, file.LastMaxSize);
    }

    [Fact]
    public async Task Client_rejects_oversized_download()
    {
        _largeDownload = true;
        var result = await Services.GetRequiredService<AttachmentsApiClient>().DownloadAsync(MeetingId, AttachmentId, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
    }

    private sealed class FakeBrowserFile(string name, byte[] bytes) : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => bytes.Length;
        public string ContentType => "text/plain";
        public long LastMaxSize { get; private set; }

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            LastMaxSize = maxAllowedSize;
            return new MemoryStream(bytes);
        }
    }
}
