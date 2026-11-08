using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using KSC.MeetingManagement.Web.Components.Meetings;
using KSC.MeetingManagement.Web.Components.Pages;
using KSC.MeetingManagement.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KSC.MeetingManagement.UnitTests;

public sealed class MeetingDetailsPageTests : BunitContext, IAsyncLifetime
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly List<(HttpMethod Method, string Path, string Body)> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

    public MeetingDetailsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(new Factory(this));
        Services.AddScoped<MeetingsApiClient>();
        Services.AddScoped<ParticipantsApiClient>();
        Services.AddScoped<UsersApiClient>();
        Services.AddSingleton(TimeProvider.System);
        Services.AddScoped<AgendaApiClient>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private sealed class Factory(MeetingDetailsPageTests owner) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(owner), disposeHandler: true) { BaseAddress = new Uri("http://api.test/") };
    }

    private sealed class Handler(MeetingDetailsPageTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/agenda", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                owner._requests.Add((request.Method, path, body));
                return Json("""{"revision":0,"items":[]}""");
            }

            if (path.Contains("/participants", StringComparison.Ordinal) || path.EndsWith("/users", StringComparison.Ordinal))
            {
                // The participants panel is covered by ParticipantsPanelTests; here it only has to stay quiet.
                return path.EndsWith("/users", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Json("[]");
            }

            owner._requests.Add((request.Method, path, body));
            return owner._respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Meeting(string status, string mode = "Online", long version = 3, string title = "هیئت مدیره") =>
        $$"""{"id":"{{Id}}","title":"{{title}}","description":"بودجه","status":"{{status}}","mode":"{{mode}}","organizerId":"{{Guid.NewGuid()}}","version":{{version}},"startUtc":"2025-03-21T05:30:00+00:00","endUtc":"2025-03-21T06:30:00+00:00","number":"2025-0007"}""";

    private (IRenderedComponent<MeetingDetailsPage> Page, IRenderedComponent<MudDialogProvider> Dialogs) RenderPage()
    {
        Render<MudPopoverProvider>();
        var dialogs = Render<MudDialogProvider>();
        return (Render<MeetingDetailsPage>(p => p.Add(c => c.MeetingId, Id)), dialogs);
    }

    private static IEnumerable<string> Buttons(IRenderedComponent<MeetingDetailsPage> page) =>
        page.FindAll("button, a.mud-button-root").Select(b => b.TextContent.Trim());

    [Fact]
    public void Shows_the_details_of_a_scheduled_online_meeting_with_its_actions()
    {
        _respond = _ => Json(Meeting("Scheduled"));

        var (page, _) = RenderPage();

        page.WaitForAssertion(() => Assert.Contains("هیئت مدیره", page.Markup));
        Assert.Contains("۲۰۲۵-۰۰۰۷", page.Markup);
        Assert.Contains("آنلاین", page.Markup);
        Assert.Contains("۰۹:۰۰–۱۰:۰۰", page.Markup);
        Assert.Contains("بودجه", page.Markup);
        var buttons = Buttons(page).ToList();
        Assert.Contains("شروع جلسه", buttons);
        Assert.Contains("به تعویق انداختن", buttons);
        Assert.Contains("لغو جلسه", buttons);
        Assert.Contains("ورود به اتاق", buttons);
        Assert.DoesNotContain("پایان جلسه", buttons);
    }

    [Theory]
    [InlineData("InProgress", new[] { "پایان جلسه" }, new[] { "شروع جلسه", "لغو جلسه", "به تعویق انداختن" })]
    [InlineData("Cancelled", new string[0], new[] { "شروع جلسه", "پایان جلسه", "لغو جلسه", "ویرایش", "ورود به اتاق" })]
    [InlineData("Completed", new string[0], new[] { "شروع جلسه", "پایان جلسه", "لغو جلسه", "ویرایش", "ورود به اتاق" })]
    [InlineData("Postponed", new[] { "لغو جلسه" }, new[] { "شروع جلسه", "پایان جلسه" })]
    public void Offers_only_the_actions_that_fit_the_status(string status, string[] present, string[] absent)
    {
        _respond = _ => Json(Meeting(status));

        var (page, _) = RenderPage();

        page.WaitForAssertion(() => Assert.Contains("هیئت مدیره", page.Markup));
        var buttons = Buttons(page).ToList();
        Assert.All(present, b => Assert.Contains(b, buttons));
        Assert.All(absent.Where(b => !(status == "InProgress" && b == "ورود به اتاق")), b => Assert.DoesNotContain(b, buttons));
    }

    [Fact]
    public void An_in_person_meeting_has_no_room_link()
    {
        _respond = _ => Json(Meeting("Scheduled", mode: "InPerson"));

        var (page, _) = RenderPage();

        page.WaitForAssertion(() => Assert.Contains("حضوری", page.Markup));
        Assert.DoesNotContain("ورود به اتاق", Buttons(page));
    }

    [Fact]
    public void Shows_an_error_with_retry_when_the_meeting_cannot_be_loaded()
    {
        _respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        var (page, _) = RenderPage();

        page.WaitForAssertion(() => Assert.Contains("جلسه پیدا نشد.", page.Markup));
        Assert.Contains("تلاش دوباره", Buttons(page));
    }

    [Fact]
    public async Task Starting_asks_for_confirmation_then_posts_the_current_version_and_shows_the_new_status()
    {
        _respond = request => request.Method == HttpMethod.Post ? Json(Meeting("InProgress", version: 4)) : Json(Meeting("Scheduled"));
        var (page, dialogs) = RenderPage();
        page.WaitForAssertion(() => Assert.Contains("شروع جلسه", Buttons(page)));

        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "شروع جلسه").Click());
        dialogs.WaitForAssertion(() => Assert.Contains("جلسه را اکنون شروع می‌کنید؟", dialogs.Markup));
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Post);
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "بله").Click());

        page.WaitForAssertion(() => Assert.Contains("پایان جلسه", Buttons(page)));
        var post = Assert.Single(_requests, r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/v1/meetings/{Id}/start", post.Path);
        Assert.Equal(3, JsonDocument.Parse(post.Body).RootElement.GetProperty("expectedVersion").GetInt64());
    }

    [Fact]
    public async Task Declining_the_confirmation_sends_nothing()
    {
        _respond = _ => Json(Meeting("Scheduled"));
        var (page, dialogs) = RenderPage();
        page.WaitForAssertion(() => Assert.Contains("به تعویق انداختن", Buttons(page)));

        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "به تعویق انداختن").Click());
        dialogs.WaitForAssertion(() => Assert.Contains("انصراف", dialogs.Markup));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "انصراف").Click());

        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task A_version_conflict_is_explained_and_the_meeting_is_reloaded()
    {
        var loads = 0;
        _respond = request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict);
            }

            loads++;
            return Json(Meeting("Scheduled", version: loads == 1 ? 3 : 5));
        };
        var (page, dialogs) = RenderPage();
        page.WaitForAssertion(() => Assert.Contains("شروع جلسه", Buttons(page)));

        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "شروع جلسه").Click());
        dialogs.WaitForAssertion(() => Assert.Contains("بله", dialogs.Markup));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "بله").Click());

        page.WaitForAssertion(() => Assert.Contains("توسط کاربر دیگری تغییر کرده است", page.Markup));
        Assert.True(loads >= 2);
    }

    private async Task<IRenderedComponent<MudDialogProvider>> OpenCancelDialogAsync()
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<CancelMeetingDialog> { { d => d.MeetingId, Id }, { d => d.Version, 3 } };
        await provider.InvokeAsync(() => dialogs.ShowAsync<CancelMeetingDialog>("t", parameters));
        return provider;
    }

    [Fact]
    public async Task Cancelling_requires_a_reason_and_posts_it_with_the_version()
    {
        _respond = _ => Json(Meeting("Cancelled", version: 4));
        var provider = await OpenCancelDialogAsync();

        await provider.InvokeAsync(() => provider.FindAll("button").Single(b => b.TextContent.Trim() == "لغو جلسه").Click());
        provider.WaitForAssertion(() => Assert.Contains("دلیل لغو را بنویسید.", provider.Markup, StringComparison.Ordinal));
        Assert.Empty(_requests);

        provider.Find("textarea").Input("  تداخل برنامه  ");
        await provider.InvokeAsync(() => provider.FindAll("button").Single(b => b.TextContent.Trim() == "لغو جلسه").Click());

        provider.WaitForAssertion(() => Assert.Single(_requests));
        var (method, path, body) = _requests[0];
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal($"/api/v1/meetings/{Id}/cancel", path);
        Assert.Equal("تداخل برنامه", doc.RootElement.GetProperty("reason").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("expectedVersion").GetInt64());
    }
}
