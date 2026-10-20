using System.Net;
using System.Reflection;
using System.Text;
using Bunit;
using KSC.MeetingManagement.Calendar;
using KSC.MeetingManagement.Web.Components.Layout;
using KSC.MeetingManagement.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace KSC.MeetingManagement.UnitTests;

public sealed class WebShellTests : BunitContext, IAsyncLifetime
{
    public WebShellTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
    }

    // MudBlazor services are async-disposable only, so the container must be disposed asynchronously.
    public Task InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void Every_menu_entry_is_served_by_a_page()
    {
        var routes = typeof(KscTheme).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
            .Select(r => r.Template)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(NavItems.Main, item => Assert.Contains(item.Href, routes));
    }

    [Fact]
    public void Menu_entries_are_unique_and_titled_in_persian()
    {
        Assert.Equal(NavItems.Main.Count, NavItems.Main.Select(i => i.Href).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(NavItems.Main, i => Assert.Contains(i.Title, c => c is >= '؀' and <= 'ۿ'));
    }

    [Fact]
    public void Layout_renders_the_menu_with_all_links_and_the_page_body()
    {
        var cut = Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<p id=\"body\">محتوا</p>"))));

        var hrefs = cut.FindAll("a.mud-nav-link").Select(a => a.GetAttribute("href")).ToList();

        Assert.Equal(NavItems.Main.Select(i => i.Href), hrefs);
        Assert.Equal("محتوا", cut.Find("#body").TextContent);
        Assert.NotEmpty(cut.FindAll("nav[aria-label='منوی اصلی']"));
        Assert.Contains(cut.FindAll("button"), b => b.GetAttribute("aria-label") == "باز و بسته کردن منو");
    }

    [Fact]
    public void Layout_runs_in_right_to_left_mode()
    {
        var cut = Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(_ => { })));

        Assert.Contains("mud-drawer-pos-right", cut.Markup, StringComparison.Ordinal);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://api.test/") };
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Meeting(int n, string status = "Scheduled", bool withTime = true) =>
        $$"""{"id":"{{Guid.NewGuid()}}","title":"m{{n}}","status":"{{status}}","mode":"Online","organizerId":"{{Guid.NewGuid()}}","version":3,"startUtc":{{(withTime ? "\"2025-03-21T05:30:00+00:00\"" : "null")}},"endUtc":{{(withTime ? "\"2025-03-21T06:30:00+00:00\"" : "null")}}}""";

    [Fact]
    public async Task Provider_requests_exactly_the_window_with_the_status_filter_and_maps_items()
    {
        var handler = new StubHandler(_ => Json($"[{Meeting(1)},{Meeting(2, "Cancelled")}]"));
        var provider = new ApiMeetingCalendarDataProvider(new StubFactory(handler));
        var from = new DateTimeOffset(2025, 3, 14, 20, 30, 0, TimeSpan.Zero);
        var to = from.AddDays(42);

        var items = await provider.GetItemsAsync(from, to, new CalendarFilter(CalendarItemStatus.Scheduled), CancellationToken.None);

        var url = Uri.UnescapeDataString(handler.Requests.Single().Query);
        Assert.Contains("from=2025-03-14T20:30:00.0000000+00:00", url, StringComparison.Ordinal);
        Assert.Contains("to=2025-04-25T20:30:00.0000000+00:00", url, StringComparison.Ordinal);
        Assert.Contains("status=Scheduled", url, StringComparison.Ordinal);
        Assert.Equal(2, items.Count);
        Assert.Equal(CalendarItemStatus.Cancelled, items[1].Status);
        Assert.Equal(3, items[0].Version);
    }

    [Fact]
    public async Task Provider_skips_meetings_without_a_time_and_follows_pages()
    {
        var full = "[" + string.Join(",", Enumerable.Range(0, 200).Select(i => Meeting(i))) + "]";
        var calls = 0;
        var handler = new StubHandler(_ => Json(++calls == 1 ? full : $"[{Meeting(900)},{Meeting(901, withTime: false)}]"));
        var provider = new ApiMeetingCalendarDataProvider(new StubFactory(handler));

        var items = await provider.GetItemsAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(30), new CalendarFilter(), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("skip=200", handler.Requests[1].Query, StringComparison.Ordinal);
        Assert.Equal(201, items.Count);
    }

    [Fact]
    public async Task Provider_surfaces_api_failures_to_the_calendar()
    {
        var provider = new ApiMeetingCalendarDataProvider(new StubFactory(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.GetItemsAsync(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), new CalendarFilter(), CancellationToken.None));
    }
}
