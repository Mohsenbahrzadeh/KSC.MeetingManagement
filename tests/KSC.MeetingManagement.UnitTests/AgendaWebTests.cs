using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using KSC.MeetingManagement.Application.Agenda;
using KSC.MeetingManagement.Web.Components.Meetings;
using KSC.MeetingManagement.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KSC.MeetingManagement.UnitTests;

public sealed class AgendaWebTests : BunitContext, IAsyncLifetime
{
    private static readonly Guid MeetingId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid First = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Second = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly List<(HttpMethod Method, string Path, string Query, string Body)> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => Json(Agenda());

    public AgendaWebTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(new Factory(this));
        Services.AddScoped<AgendaApiClient>();
        Services.AddScoped<UsersApiClient>();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private sealed class Factory(AgendaWebTests owner) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(owner), disposeHandler: true) { BaseAddress = new Uri("http://api.test/") };
    }

    private sealed class Handler(AgendaWebTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            owner._requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query, body));
            return owner._respond(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Agenda(bool items = false) => items
        ? $$"""{"revision":7,"items":[{"id":"{{First}}","order":1,"title":"موضوع اول","description":null,"presenterUserId":null,"durationMinutes":30},{"id":"{{Second}}","order":2,"title":"موضوع دوم","description":null,"presenterUserId":null,"durationMinutes":null}]}"""
        : """{"revision":7,"items":[]}""";

    [Fact]
    public async Task Client_sends_revision_and_fields_to_meeting_scoped_endpoints()
    {
        var client = Services.GetRequiredService<AgendaApiClient>();
        var input = new AgendaItemInput("موضوع", "توضیح", null, 30);
        await client.AddAsync(MeetingId, 7, input, CancellationToken.None);
        await client.UpdateAsync(MeetingId, First, 8, input, CancellationToken.None);
        await client.RemoveAsync(MeetingId, First, 9, CancellationToken.None);
        await client.ReorderAsync(MeetingId, 10, [Second, First], CancellationToken.None);

        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Put }, _requests.Select(r => r.Method));
        Assert.Equal($"/api/v1/meetings/{MeetingId}/agenda", _requests[0].Path);
        Assert.Equal($"/api/v1/meetings/{MeetingId}/agenda/{First}", _requests[1].Path);
        Assert.Equal("?expectedRevision=9", _requests[2].Query);
        Assert.Equal($"/api/v1/meetings/{MeetingId}/agenda/order", _requests[3].Path);
        using var add = JsonDocument.Parse(_requests[0].Body);
        Assert.Equal(7, add.RootElement.GetProperty("expectedRevision").GetInt64());
        Assert.Equal("موضوع", add.RootElement.GetProperty("title").GetString());
        using var order = JsonDocument.Parse(_requests[3].Body);
        Assert.Equal(10, order.RootElement.GetProperty("expectedRevision").GetInt64());
        Assert.Equal(new[] { Second, First }, order.RootElement.GetProperty("itemIds").EnumerateArray().Select(e => e.GetGuid()));
    }

    [Fact]
    public async Task Panel_shows_empty_state_and_can_reorder_with_current_revision()
    {
        _respond = request => request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/agenda", StringComparison.Ordinal)
            ? Json(Agenda(items: true))
            : Json(Agenda(items: true));
        Render<MudBlazor.MudPopoverProvider>();
        var panel = Render<AgendaPanel>(p => p.Add(c => c.MeetingId, MeetingId).Add(c => c.CanEdit, true));
        panel.WaitForAssertion(() => Assert.Contains("موضوع دوم", panel.Markup));

        await panel.InvokeAsync(() => panel.FindAll("button").Single(b => b.GetAttribute("aria-label") == "انتقال موضوع دوم به بالا").Click());

        panel.WaitForAssertion(() => Assert.Contains(_requests, r => r.Path.EndsWith("/order", StringComparison.Ordinal)));
        var request = _requests.Single(r => r.Path.EndsWith("/order", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(7, body.RootElement.GetProperty("expectedRevision").GetInt64());
        Assert.Equal(new[] { Second, First }, body.RootElement.GetProperty("itemIds").EnumerateArray().Select(e => e.GetGuid()));
    }

    [Fact]
    public void Empty_and_read_only_states_hide_mutations()
    {
        Render<MudBlazor.MudPopoverProvider>();
        var empty = Render<AgendaPanel>(p => p.Add(c => c.MeetingId, MeetingId).Add(c => c.CanEdit, true));
        empty.WaitForAssertion(() => Assert.Contains("هنوز موضوعی", empty.Markup));
        Assert.Contains("افزودن موضوع", empty.Markup);

        _respond = _ => Json(Agenda(items: true));
        var readOnly = Render<AgendaPanel>(p => p.Add(c => c.MeetingId, MeetingId).Add(c => c.CanEdit, false));
        readOnly.WaitForAssertion(() => Assert.Contains("موضوع اول", readOnly.Markup));
        Assert.DoesNotContain("افزودن موضوع", readOnly.Markup);
        Assert.DoesNotContain("حذف موضوع اول", readOnly.Markup);
    }

    [Fact]
    public async Task Add_dialog_validates_title_and_submits_current_revision()
    {
        _respond = request => request.RequestUri!.AbsolutePath.EndsWith("/members", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Json(Agenda());
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<AgendaItemDialog>
        {
            { d => d.MeetingId, MeetingId }, { d => d.Revision, 7L },
        };
        await provider.InvokeAsync(() => dialogs.ShowAsync<AgendaItemDialog>("موضوع جدید", parameters));
        provider.WaitForAssertion(() => Assert.Contains("ذخیره", provider.Markup));
        await provider.InvokeAsync(() => provider.FindAll("button").Single(b => b.TextContent.Trim() == "ذخیره").Click());
        provider.WaitForAssertion(() => Assert.Contains("موضوع را وارد کنید.", provider.Markup));
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Post);

        provider.Find("input").Change("موضوع تازه");
        await provider.InvokeAsync(() => provider.FindAll("button").Single(b => b.TextContent.Trim() == "ذخیره").Click());
        provider.WaitForAssertion(() => Assert.Contains(_requests, r => r.Method == HttpMethod.Post));
        var request = _requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"/api/v1/meetings/{MeetingId}/agenda", request.Path);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(7, body.RootElement.GetProperty("expectedRevision").GetInt64());
        Assert.Equal("موضوع تازه", body.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Version_conflict_refreshes_the_agenda_without_replaying_the_change()
    {
        var loads = 0;
        _respond = request =>
        {
            if (request.Method == HttpMethod.Put) return new HttpResponseMessage(HttpStatusCode.Conflict);
            loads++;
            return Json(Agenda(items: true));
        };
        Render<MudBlazor.MudPopoverProvider>();
        var panel = Render<AgendaPanel>(p => p.Add(c => c.MeetingId, MeetingId).Add(c => c.CanEdit, true));
        panel.WaitForAssertion(() => Assert.Contains("موضوع دوم", panel.Markup));
        await panel.InvokeAsync(() => panel.FindAll("button").Single(b => b.GetAttribute("aria-label") == "انتقال موضوع دوم به بالا").Click());
        panel.WaitForAssertion(() => Assert.Contains("توسط کاربر دیگری", panel.Markup));
        Assert.Equal(2, loads);
        Assert.Single(_requests, r => r.Method == HttpMethod.Put);
    }
}
