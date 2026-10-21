using System.Net;
using System.Text;
using Bunit;
using KSC.MeetingManagement.Web.Components.Pages;
using KSC.MeetingManagement.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KSC.MeetingManagement.UnitTests;

public sealed class MeetingsPageTests : BunitContext, IAsyncLifetime
{
    private readonly List<string> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

    public MeetingsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(new Factory(this));
        Services.AddScoped<MeetingsApiClient>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private sealed class Factory(MeetingsPageTests owner) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(owner), disposeHandler: true) { BaseAddress = new Uri("http://api.test/") };
    }

    private sealed class Handler(MeetingsPageTests owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner._requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(owner._respond(request));
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Meeting(string title, string status, string mode, string number) =>
        $$"""{"id":"{{Guid.NewGuid()}}","title":"{{title}}","status":"{{status}}","mode":"{{mode}}","organizerId":"{{Guid.NewGuid()}}","version":1,"startUtc":"2025-03-21T05:30:00+00:00","endUtc":"2025-03-21T06:30:00+00:00","number":"{{number}}"}""";

    private IRenderedComponent<MeetingsPage> RenderPage()
    {
        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        return Render<MeetingsPage>();
    }

    [Fact]
    public void Lists_meetings_with_persian_number_status_and_tehran_time()
    {
        _respond = _ => Json($"[{Meeting("بودجه", "Scheduled", "Online", "2025-0001")},{Meeting("جلسهٔ حضوری", "Cancelled", "InPerson", "2025-0002")}]");

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tbody tr").Count));
        var text = cut.Markup;
        Assert.Contains("بودجه", text);
        Assert.Contains("۲۰۲۵-۰۰۰۱", text);
        Assert.Contains("آنلاین", text);
        Assert.Contains("۰۹:۰۰–۱۰:۰۰", text);
        Assert.Contains("api/v1/meetings?skip=0&take=50", Assert.Single(_requests));
    }

    [Fact]
    public void Only_online_scheduled_meetings_offer_a_room_link()
    {
        _respond = _ => Json($"[{Meeting("آنلاین", "Scheduled", "Online", "1")},{Meeting("لغوشده", "Cancelled", "Online", "2")},{Meeting("حضوری", "Scheduled", "InPerson", "3")}]");

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("tbody tr").Count));
        var links = cut.FindAll("a").Where(a => a.GetAttribute("href")?.EndsWith("/room", StringComparison.Ordinal) == true).ToList();
        Assert.Single(links);
        Assert.Contains("آنلاین", links[0].GetAttribute("aria-label"));
    }

    [Fact]
    public void Shows_empty_state_when_there_are_no_meetings()
    {
        _respond = _ => Json("[]");

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Contains("جلسه‌ای پیدا نشد.", cut.Markup));
        Assert.Empty(cut.FindAll("tbody tr"));
    }

    [Fact]
    public void Shows_a_generic_error_with_retry_when_the_api_fails_and_recovers_on_retry()
    {
        _respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Contains("عملیات انجام نشد", cut.Markup));

        _respond = _ => Json($"[{Meeting("بازیابی", "Scheduled", "Online", "1")}]");
        cut.Find("button.mud-button-text").Click();

        cut.WaitForAssertion(() => Assert.Contains("بازیابی", cut.Markup));
    }

    [Fact]
    public void Offers_more_when_a_full_page_is_returned_and_requests_the_next_page()
    {
        var page = string.Join(',', Enumerable.Range(0, 50).Select(i => Meeting("جلسه " + i, "Scheduled", "InPerson", i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        _respond = request => request.RequestUri!.Query.Contains("skip=50", StringComparison.Ordinal) ? Json("[]") : Json($"[{page}]");

        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Equal(50, cut.FindAll("tbody tr").Count));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "بیشتر").Click();

        cut.WaitForAssertion(() => Assert.Contains(_requests, r => r.Contains("skip=50&take=50", StringComparison.Ordinal)));
        cut.WaitForAssertion(() => Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "بیشتر"));
    }
}
