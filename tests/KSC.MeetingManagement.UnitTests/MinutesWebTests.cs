using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using KSC.MeetingManagement.Web.Components.Pages;
using KSC.MeetingManagement.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KSC.MeetingManagement.UnitTests;

public sealed class MinutesWebTests : BunitContext, IAsyncLifetime
{
    private static readonly Guid MeetingId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly List<(HttpMethod Method, string Path, string Body)> _requests = [];
    private string _status = "Draft";
    private int _version = 1;
    private string[] _roles = ["Secretary"];
    private HttpStatusCode? _failure;
    private HttpStatusCode? _writeFailure;

    public MinutesWebTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(new FakeFactory(new Handler(this)));
        Services.AddScoped<MinutesApiClient>();
        Services.AddScoped<AuthApiClient>();
        Services.AddSingleton<ISessionStore>(new MemoryStore());
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        Services.AddScoped<SessionState>();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private sealed class Handler(MinutesWebTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            owner._requests.Add((request.Method, path, body));
            if (path.EndsWith("/login", StringComparison.Ordinal))
            {
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
                        memberships = new[] { new { tenantId = TenantId, tenantName = "Tenant", tenantSlug = "tenant", roles = owner._roles } }
                    },
                }));
            }
            if (owner._failure is { } failure) return new HttpResponseMessage(failure);
            if (request.Method == HttpMethod.Put && owner._writeFailure is { } writeFailure) return new HttpResponseMessage(writeFailure);
            if (path.EndsWith("/versions", StringComparison.Ordinal)) return Json("[]");
            if (path.EndsWith("/versions/1", StringComparison.Ordinal)) return Json("""{"number":1,"content":"متن اولیه","authorUserId":"22222222-2222-2222-2222-222222222222","createdAtUtc":"2026-01-01T00:00:00Z"}""");
            if (request.Method == HttpMethod.Put)
            {
                owner._version++;
                owner._status = "Draft";
            }
            if (path.EndsWith("/submit", StringComparison.Ordinal)) owner._status = "InReview";
            if (path.EndsWith("/approve", StringComparison.Ordinal)) owner._status = "Approved";
            if (path.EndsWith("/reject", StringComparison.Ordinal)) owner._status = "Rejected";
            if (path.EndsWith("/reopen", StringComparison.Ordinal)) owner._status = "Draft";
            var content = request.Method == HttpMethod.Put ? JsonDocument.Parse(body).RootElement.GetProperty("content").GetString() : "متن اولیه";
            return Json(JsonSerializer.Serialize(new
            {
                id = Guid.NewGuid(),
                meetingId = MeetingId,
                status = owner._status,
                currentVersionNumber = owner._version,
                approvedVersionNumber = (int?)null,
                content,
                transitions = Array.Empty<object>(),
            }));
        }
    }

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private async Task<IRenderedComponent<MinutesPage>> RenderPageAsync()
    {
        await Services.GetRequiredService<SessionState>().LoginAsync("user@example.com", "password", CancellationToken.None);
        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        return Render<MinutesPage>(p => p.Add(c => c.MeetingId, MeetingId));
    }

    [Fact]
    public async Task Secretary_saves_with_expected_version_and_can_submit_but_not_approve()
    {
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("ویرایش متن", page.Markup));
        Assert.DoesNotContain("بازگشایی", page.Markup);
        Assert.DoesNotContain("تأیید</button>", page.Markup);
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "ویرایش متن").Click());
        page.Find("textarea").Change("متن جدید");
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "ذخیرهٔ نسخه").Click());
        page.WaitForAssertion(() => Assert.Contains(_requests, r => r.Method == HttpMethod.Put));
        var save = _requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal($"/api/v1/meetings/{MeetingId}/minutes", save.Path);
        using var body = JsonDocument.Parse(save.Body);
        Assert.Equal(1, body.RootElement.GetProperty("expectedVersionNumber").GetInt32());
        Assert.Equal("متن جدید", body.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Approver_sees_review_controls_but_cannot_edit()
    {
        _roles = ["Approver"];
        _status = "InReview";
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("در انتظار تأیید", page.Markup));
        Assert.Contains("تأیید", page.Markup);
        Assert.Contains("رد", page.Markup);
        Assert.DoesNotContain("ویرایش متن", page.Markup);
        Assert.DoesNotContain("ارسال برای تأیید", page.Markup);
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "رد").Click());
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "ثبت").Click());
        page.WaitForAssertion(() => Assert.Contains("دلیل را وارد کنید.", page.Markup));
        Assert.DoesNotContain(_requests, r => r.Path.EndsWith("/reject", StringComparison.Ordinal));
        page.Find("textarea").Change("نیاز به اصلاح");
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "ثبت").Click());
        page.WaitForAssertion(() => Assert.Contains(_requests, r => r.Path.EndsWith("/reject", StringComparison.Ordinal)));
        using var body = JsonDocument.Parse(_requests.Single(r => r.Path.EndsWith("/reject", StringComparison.Ordinal)).Body);
        Assert.Equal("نیاز به اصلاح", body.RootElement.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task Absent_minutes_show_creation_only_to_writers()
    {
        _failure = HttpStatusCode.NotFound;
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("نوشتن صورت‌جلسه", page.Markup));

        var session = Services.GetRequiredService<SessionState>();
        await session.LogoutAsync(CancellationToken.None);
        _roles = ["Participant"];
        await session.LoginAsync("user@example.com", "password", CancellationToken.None);
        var participant = Render<MinutesPage>(p => p.Add(c => c.MeetingId, MeetingId));
        participant.WaitForAssertion(() => Assert.Contains("صورت‌جلسه‌ای برای نمایش وجود ندارد", participant.Markup));
        Assert.DoesNotContain("نوشتن صورت‌جلسه", participant.Markup);
    }

    [Fact]
    public async Task Conflict_refreshes_version_without_dropping_the_unsaved_text()
    {
        _writeFailure = HttpStatusCode.Conflict;
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("ویرایش متن", page.Markup));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "ویرایش متن").Click());
        page.Find("textarea").Change("متن ذخیره‌نشده");
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "ذخیرهٔ نسخه").Click());
        page.WaitForAssertion(() => Assert.Contains("توسط کاربر دیگری", page.Markup));
        Assert.Contains("متن ذخیره‌نشده", page.Find("textarea").TextContent);
        Assert.Single(_requests, r => r.Method == HttpMethod.Put);
    }
}
