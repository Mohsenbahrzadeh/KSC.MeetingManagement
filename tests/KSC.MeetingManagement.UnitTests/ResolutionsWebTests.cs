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

public sealed class ResolutionsWebTests : BunitContext, IAsyncLifetime
{
    private static readonly Guid MeetingId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ResolutionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly List<(HttpMethod Method, string Path, string Body)> _requests = [];
    private string[] _roles = ["Secretary"];
    private string _status = "Draft";
    private long _version = 3;
    private bool _empty;
    private bool _conflict;
    private IRenderedComponent<MudDialogProvider>? _dialogs;

    public ResolutionsWebTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(new FakeFactory(new Handler(this)));
        Services.AddScoped<ResolutionsApiClient>();
        Services.AddScoped<AgendaApiClient>();
        Services.AddScoped<AuthApiClient>();
        Services.AddSingleton<ISessionStore>(new MemoryStore());
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        Services.AddScoped<SessionState>();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private sealed class Handler(ResolutionsWebTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            owner._requests.Add((request.Method, path, body));
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
            if (path.EndsWith("/agenda", StringComparison.Ordinal)) return Json("""{"revision":0,"items":[]}""");
            if (request.Method == HttpMethod.Get) return Json(owner._empty ? "[]" : $"[{owner.Item()}]");
            if (owner._conflict) return new HttpResponseMessage(HttpStatusCode.Conflict);
            if (path.EndsWith("/adopt", StringComparison.Ordinal)) owner._status = "Adopted";
            if (path.EndsWith("/reject", StringComparison.Ordinal)) owner._status = "Rejected";
            if (path.EndsWith("/revoke", StringComparison.Ordinal)) owner._status = "Revoked";
            owner._version++;
            return Json(owner.Item(), request.Method == HttpMethod.Post && path.EndsWith("/resolutions", StringComparison.Ordinal)
                ? HttpStatusCode.Created : HttpStatusCode.OK);
        }
    }

    private string Item() => JsonSerializer.Serialize(new
    {
        id = ResolutionId,
        meetingId = MeetingId,
        number = 1,
        text = "مصوبهٔ نمونه",
        agendaItemId = (Guid?)null,
        status = _status,
        decidedAtUtc = (DateTimeOffset?)null,
        decidedByUserId = (Guid?)null,
        revocationReason = (string?)null,
        lockVersion = _version,
    });

    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private async Task<IRenderedComponent<ResolutionsPage>> RenderPageAsync()
    {
        await Services.GetRequiredService<SessionState>().LoginAsync("user@example.com", "password", CancellationToken.None);
        Render<MudPopoverProvider>();
        _dialogs = Render<MudDialogProvider>();
        return Render<ResolutionsPage>(p => p.Add(c => c.MeetingId, MeetingId));
    }

    [Fact]
    public async Task Writer_can_decide_with_lock_version_but_cannot_revoke()
    {
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("مصوبهٔ نمونه", page.Markup));
        Assert.Contains("مصوبهٔ جدید", page.Markup);
        Assert.DoesNotContain("ابطال مصوبه", page.Markup);
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.GetAttribute("aria-label") == "تصویب مصوبهٔ 1").Click());
        Assert.DoesNotContain(_requests, r => r.Path.EndsWith("/adopt", StringComparison.Ordinal));
        var provider = _dialogs!;
        provider.WaitForAssertion(() => Assert.Contains("مصوبهٔ 1 تصویب شود؟", provider.Markup));
        await provider.InvokeAsync(() => provider.FindAll("button").Single(b => b.TextContent.Trim() == "بله").Click());
        page.WaitForAssertion(() => Assert.Contains(_requests, r => r.Path.EndsWith("/adopt", StringComparison.Ordinal)));
        var post = _requests.Single(r => r.Path.EndsWith("/adopt", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(post.Body);
        Assert.Equal(3, body.RootElement.GetProperty("expectedLockVersion").GetInt64());
    }

    [Fact]
    public async Task Approver_can_revoke_adopted_resolution_only_with_a_reason()
    {
        _roles = ["Approver"];
        _status = "Adopted";
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("ابطال مصوبهٔ 1", page.Markup));
        Assert.DoesNotContain("مصوبهٔ جدید", page.Markup);
        Assert.DoesNotContain("ویرایش مصوبهٔ 1", page.Markup);
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.GetAttribute("aria-label") == "ابطال مصوبهٔ 1").Click());
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "ثبت ابطال").Click());
        page.WaitForAssertion(() => Assert.Contains("دلیل ابطال را وارد کنید.", page.Markup));
        Assert.DoesNotContain(_requests, r => r.Path.EndsWith("/revoke", StringComparison.Ordinal));
        page.Find("textarea").Change("دلیل آزمون");
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "ثبت ابطال").Click());
        page.WaitForAssertion(() => Assert.Contains(_requests, r => r.Path.EndsWith("/revoke", StringComparison.Ordinal)));
        using var body = JsonDocument.Parse(_requests.Single(r => r.Path.EndsWith("/revoke", StringComparison.Ordinal)).Body);
        Assert.Equal(3, body.RootElement.GetProperty("expectedLockVersion").GetInt64());
        Assert.Equal("دلیل آزمون", body.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Participant_sees_read_only_list_and_empty_state()
    {
        _roles = ["Participant"];
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("مصوبهٔ نمونه", page.Markup));
        Assert.DoesNotContain("مصوبهٔ جدید", page.Markup);
        Assert.DoesNotContain("تصویب مصوبهٔ", page.Markup);
    }

    [Fact]
    public async Task Empty_list_offers_creation_to_writer()
    {
        _empty = true;
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("مصوبه‌ای برای این جلسه ثبت نشده است", page.Markup));
        Assert.Contains("مصوبهٔ جدید", page.Markup);
    }

    [Fact]
    public async Task Create_dialog_validates_text_and_posts_to_meeting_endpoint()
    {
        _empty = true;
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("مصوبهٔ جدید", page.Markup));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "مصوبهٔ جدید").Click());
        var provider = _dialogs!;
        provider.WaitForAssertion(() => Assert.Contains("متن مصوبه", provider.Markup));
        await provider.InvokeAsync(() => provider.FindAll("button").Single(b => b.TextContent.Trim() == "ذخیره").Click());
        provider.WaitForAssertion(() => Assert.Contains("متن مصوبه را وارد کنید.", provider.Markup));
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Post && r.Path.EndsWith("/resolutions", StringComparison.Ordinal));

        provider.Find("textarea").Change("متن مصوبهٔ تازه");
        await provider.InvokeAsync(() => provider.FindAll("button").Single(b => b.TextContent.Trim() == "ذخیره").Click());
        provider.WaitForAssertion(() => Assert.Contains(_requests, r => r.Method == HttpMethod.Post && r.Path.EndsWith("/resolutions", StringComparison.Ordinal)));
        var post = _requests.Single(r => r.Method == HttpMethod.Post && r.Path.EndsWith("/resolutions", StringComparison.Ordinal));
        Assert.Equal($"/api/v1/meetings/{MeetingId}/resolutions", post.Path);
        using var body = JsonDocument.Parse(post.Body);
        Assert.Equal("متن مصوبهٔ تازه", body.RootElement.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("agendaItemId").ValueKind);
    }

    [Fact]
    public async Task Conflict_refreshes_list_without_repeating_decision()
    {
        _conflict = true;
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("مصوبهٔ نمونه", page.Markup));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.GetAttribute("aria-label") == "تصویب مصوبهٔ 1").Click());
        var provider = _dialogs!;
        provider.WaitForAssertion(() => Assert.Contains("بله", provider.Markup));
        await provider.InvokeAsync(() => provider.FindAll("button").Single(b => b.TextContent.Trim() == "بله").Click());
        page.WaitForAssertion(() => Assert.Contains("توسط کاربر دیگری", page.Markup));
        Assert.Single(_requests, r => r.Path.EndsWith("/adopt", StringComparison.Ordinal));
    }
}
