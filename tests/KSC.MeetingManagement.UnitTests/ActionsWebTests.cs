using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using KSC.MeetingManagement.Application.Actions;
using KSC.MeetingManagement.Web.Components.Pages;
using KSC.MeetingManagement.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KSC.MeetingManagement.UnitTests;

public sealed class ActionsWebTests : BunitContext, IAsyncLifetime
{
    private static readonly Guid MeetingId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid UserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ActionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private readonly List<(HttpMethod Method, string Path, string Body)> _requests = [];
    private string[] _roles = ["Secretary"];
    private Guid _owner = UserId;
    private string _status = "Open";
    private long _version = 3;
    private bool _empty;
    private bool _conflict;
    private IRenderedComponent<MudDialogProvider>? _dialogs;

    public ActionsWebTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(new FakeFactory(new Handler(this)));
        Services.AddScoped<ActionsApiClient>();
        Services.AddScoped<ResolutionsApiClient>();
        Services.AddScoped<UsersApiClient>();
        Services.AddScoped<AuthApiClient>();
        Services.AddSingleton<ISessionStore>(new MemoryStore());
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        Services.AddScoped<SessionState>();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private sealed class Handler(ActionsWebTests owner) : HttpMessageHandler
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
                        id = UserId,
                        email = "user@example.com",
                        displayName = "User",
                        memberships = new[] { new { tenantId = TenantId, tenantName = "Tenant", tenantSlug = "tenant", roles = owner._roles } },
                    },
                }));
            if (path.EndsWith("/users", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.Forbidden);
            if (path.EndsWith("/resolutions", StringComparison.Ordinal)) return Json("[]");
            if (request.Method == HttpMethod.Get) return Json(owner._empty ? "[]" : $"[{owner.Item()}]");
            if (owner._conflict) return new HttpResponseMessage(HttpStatusCode.Conflict);
            if (path.EndsWith("/start", StringComparison.Ordinal)) owner._status = "InProgress";
            if (path.EndsWith("/complete", StringComparison.Ordinal)) owner._status = "Done";
            if (path.EndsWith("/cancel", StringComparison.Ordinal)) owner._status = "Cancelled";
            owner._version++;
            return Json(owner.Item(), request.Method == HttpMethod.Post && path.EndsWith("/actions", StringComparison.Ordinal)
                ? HttpStatusCode.Created : HttpStatusCode.OK);
        }
    }

    private string Item() => JsonSerializer.Serialize(new
    {
        id = ActionId,
        meetingId = MeetingId,
        resolutionId = (Guid?)null,
        title = "پیگیری قرارداد",
        description = "شرح",
        ownerUserId = _owner,
        dueDate = new DateOnly(2027, 1, 1),
        status = _status,
        isOverdue = false,
        completedAtUtc = (DateTimeOffset?)null,
        completedByUserId = (Guid?)null,
        note = (string?)null,
        lockVersion = _version,
    });

    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private async Task<IRenderedComponent<ActionsPage>> RenderPageAsync()
    {
        await Services.GetRequiredService<SessionState>().LoginAsync("user@example.com", "password", CancellationToken.None);
        Render<MudPopoverProvider>();
        _dialogs = Render<MudDialogProvider>();
        return Render<ActionsPage>(p => p.Add(c => c.MeetingId, MeetingId));
    }

    [Fact]
    public async Task Owner_can_start_with_expected_version_but_cannot_manage()
    {
        _roles = ["Participant"];
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("پیگیری قرارداد", page.Markup));
        Assert.DoesNotContain("اقدام جدید", page.Markup);
        Assert.DoesNotContain("ویرایش پیگیری قرارداد", page.Markup);
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.GetAttribute("aria-label") == "شروع پیگیری قرارداد").Click());
        Assert.DoesNotContain(_requests, r => r.Path.EndsWith("/start", StringComparison.Ordinal));
        var dialogs = _dialogs!;
        dialogs.WaitForAssertion(() => Assert.Contains("شروع شود؟", dialogs.Markup));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "بله").Click());
        page.WaitForAssertion(() => Assert.Contains(_requests, r => r.Path.EndsWith("/start", StringComparison.Ordinal)));
        using var body = JsonDocument.Parse(_requests.Single(r => r.Path.EndsWith("/start", StringComparison.Ordinal)).Body);
        Assert.Equal(3, body.RootElement.GetProperty("expectedLockVersion").GetInt64());
    }

    [Fact]
    public async Task Nonowner_cannot_operate_action()
    {
        _roles = ["Participant"];
        _owner = Guid.NewGuid();
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("پیگیری قرارداد", page.Markup));
        Assert.DoesNotContain("شروع پیگیری قرارداد", page.Markup);
        Assert.DoesNotContain("تکمیل پیگیری قرارداد", page.Markup);
    }

    [Fact]
    public async Task Manager_must_give_reason_to_cancel()
    {
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("لغو پیگیری قرارداد", page.Markup));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.GetAttribute("aria-label") == "لغو پیگیری قرارداد").Click());
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "ثبت").Click());
        page.WaitForAssertion(() => Assert.Contains("دلیل لغو را وارد کنید.", page.Markup));
        Assert.DoesNotContain(_requests, r => r.Path.EndsWith("/cancel", StringComparison.Ordinal));
        page.Find("textarea").Change("تغییر برنامه");
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "ثبت").Click());
        page.WaitForAssertion(() => Assert.Contains(_requests, r => r.Path.EndsWith("/cancel", StringComparison.Ordinal)));
        using var body = JsonDocument.Parse(_requests.Single(r => r.Path.EndsWith("/cancel", StringComparison.Ordinal)).Body);
        Assert.Equal(3, body.RootElement.GetProperty("expectedLockVersion").GetInt64());
        Assert.Equal("تغییر برنامه", body.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Empty_list_shows_create_action_to_manager()
    {
        _empty = true;
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("اقدامی برای این جلسه ثبت نشده است", page.Markup));
        Assert.Contains("اقدام جدید", page.Markup);
    }

    [Fact]
    public async Task Create_dialog_validates_title_and_defaults_owner_to_signed_in_user()
    {
        _empty = true;
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("اقدام جدید", page.Markup));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "اقدام جدید").Click());
        var dialogs = _dialogs!;
        dialogs.WaitForAssertion(() => Assert.Contains("عنوان اقدام", dialogs.Markup));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "ذخیره").Click());
        dialogs.WaitForAssertion(() => Assert.Contains("عنوان اقدام را وارد کنید.", dialogs.Markup));
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Post && r.Path.EndsWith("/actions", StringComparison.Ordinal));

        dialogs.Find("input.mud-input-slot").Change("اقدام تازه");
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "ذخیره").Click());
        dialogs.WaitForAssertion(() => Assert.Contains(_requests, r => r.Method == HttpMethod.Post && r.Path.EndsWith("/actions", StringComparison.Ordinal)));
        using var body = JsonDocument.Parse(_requests.Single(r => r.Method == HttpMethod.Post && r.Path.EndsWith("/actions", StringComparison.Ordinal)).Body);
        Assert.Equal("اقدام تازه", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(UserId, body.RootElement.GetProperty("ownerUserId").GetGuid());
    }

    [Fact]
    public async Task Conflict_refreshes_without_repeating_mutation()
    {
        _conflict = true;
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("پیگیری قرارداد", page.Markup));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.GetAttribute("aria-label") == "شروع پیگیری قرارداد").Click());
        var dialogs = _dialogs!;
        dialogs.WaitForAssertion(() => Assert.Contains("بله", dialogs.Markup));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "بله").Click());
        page.WaitForAssertion(() => Assert.Contains("توسط کاربر دیگری", page.Markup));
        Assert.Single(_requests, r => r.Path.EndsWith("/start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Client_sends_expected_update_and_completion_contracts()
    {
        var client = Services.GetRequiredService<ActionsApiClient>();
        var input = new ActionItemInput("عنوان", null, UserId, new DateOnly(2027, 1, 1), null);
        await client.UpdateAsync(MeetingId, ActionId, 4, input, CancellationToken.None);
        await client.CompleteAsync(MeetingId, ActionId, 5, "انجام شد", CancellationToken.None);

        var update = _requests.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal($"/api/v1/meetings/{MeetingId}/actions/{ActionId}", update.Path);
        using var updateBody = JsonDocument.Parse(update.Body);
        Assert.Equal(4, updateBody.RootElement.GetProperty("expectedLockVersion").GetInt64());
        Assert.Equal("2027-01-01", updateBody.RootElement.GetProperty("dueDate").GetString());
        var complete = _requests.Single(r => r.Path.EndsWith("/complete", StringComparison.Ordinal));
        using var completeBody = JsonDocument.Parse(complete.Body);
        Assert.Equal(5, completeBody.RootElement.GetProperty("expectedLockVersion").GetInt64());
        Assert.Equal("انجام شد", completeBody.RootElement.GetProperty("note").GetString());
    }
}
