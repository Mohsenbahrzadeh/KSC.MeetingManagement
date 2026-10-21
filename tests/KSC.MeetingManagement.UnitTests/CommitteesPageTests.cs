using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bunit;
using KSC.MeetingManagement.Application.Committees;
using KSC.MeetingManagement.Web.Components.Committees;
using KSC.MeetingManagement.Web.Components.Pages;
using KSC.MeetingManagement.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KSC.MeetingManagement.UnitTests;

public sealed class CommitteesPageTests : BunitContext, IAsyncLifetime
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly List<(HttpMethod Method, string Path, string Body)> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

    public CommitteesPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(new Factory(this));
        Services.AddScoped<CommitteesApiClient>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private sealed class Factory(CommitteesPageTests owner) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(owner), disposeHandler: true) { BaseAddress = new Uri("http://api.test/") };
    }

    private sealed class Handler(CommitteesPageTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            owner._requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return owner._respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Committee(string name, bool active = true, int members = 0, string? description = null) =>
        $$"""{"id":"{{Guid.NewGuid()}}","name":"{{name}}","description":{{(description is null ? "null" : "\"" + description + "\"")}},"isActive":{{(active ? "true" : "false")}},"members":[{{string.Join(',', Enumerable.Range(0, members).Select(_ => $$"""{"userId":"{{Guid.NewGuid()}}","role":"Member"}"""))}}]}""";

    private IRenderedComponent<CommitteesPage> RenderPage()
    {
        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        return Render<CommitteesPage>();
    }

    [Fact]
    public void Lists_committees_with_member_count_and_active_state()
    {
        _respond = _ => Json($"[{Committee("کمیتهٔ مالی", members: 3, description: "بودجه")},{Committee("کمیتهٔ قدیمی", active: false)}]");

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tbody tr").Count));
        Assert.Contains("کمیتهٔ مالی", cut.Markup);
        Assert.Contains("بودجه", cut.Markup);
        Assert.Contains("۳", cut.Markup);
        Assert.Contains("فعال", cut.Markup);
        Assert.Contains("غیرفعال", cut.Markup);
    }

    [Fact]
    public void Shows_empty_state_when_there_are_no_committees()
    {
        _respond = _ => Json("[]");

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Contains("هنوز کمیته‌ای تعریف نشده است.", cut.Markup));
    }

    [Fact]
    public void Shows_a_generic_error_and_recovers_on_retry()
    {
        _respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Contains("عملیات انجام نشد", cut.Markup));

        _respond = _ => Json($"[{Committee("بازیابی")}]");
        cut.Find("button.mud-button-text").Click();

        cut.WaitForAssertion(() => Assert.Contains("بازیابی", cut.Markup));
    }

    private async Task<IRenderedComponent<MudDialogProvider>> OpenDialogAsync(CommitteeDto? committee)
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<CommitteeFormDialog> { { d => d.Committee, committee } };
        await provider.InvokeAsync(() => dialogs.ShowAsync<CommitteeFormDialog>("t", parameters));
        return provider;
    }

    private static Task ClickAsync(IRenderedComponent<MudDialogProvider> provider, string text) =>
        provider.InvokeAsync(() => provider.FindAll("button").Single(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click());

    [Fact]
    public async Task Creating_a_committee_posts_the_trimmed_name_and_description()
    {
        _respond = _ => Json(Committee("کمیتهٔ جدید"), HttpStatusCode.Created);
        var provider = await OpenDialogAsync(null);

        provider.FindAll("input")[0].Input("  کمیتهٔ جدید  ");
        await ClickAsync(provider, "ذخیره");

        provider.WaitForAssertion(() => Assert.Single(_requests));
        var (method, path, body) = _requests[0];
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/api/v1/committees", path);
        Assert.Equal("کمیتهٔ جدید", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_blank_name_is_rejected_without_calling_the_api()
    {
        var provider = await OpenDialogAsync(null);

        await ClickAsync(provider, "ذخیره");

        provider.WaitForAssertion(() => Assert.Contains("نام کمیته را وارد کنید.", provider.Markup, StringComparison.Ordinal));
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Editing_puts_the_existing_committee_with_its_active_flag()
    {
        var existing = JsonSerializer.Deserialize<CommitteeDto>(Committee("قدیمی", active: false), ReadOptions)!;
        _respond = _ => Json(Committee("قدیمی"));
        var provider = await OpenDialogAsync(existing);

        await ClickAsync(provider, "ذخیره");

        provider.WaitForAssertion(() => Assert.Single(_requests));
        var (method, path, body) = _requests[0];
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(HttpMethod.Put, method);
        Assert.Equal($"/api/v1/committees/{existing.Id}", path);
        Assert.False(doc.RootElement.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task A_validation_error_from_the_api_is_shown_in_the_dialog()
    {
        _respond = _ => Json("""{"title":"x","status":422,"detail":"نام کمیته تکراری است."}""", HttpStatusCode.UnprocessableEntity);
        var provider = await OpenDialogAsync(null);

        provider.FindAll("input")[0].Input("تکراری");
        await ClickAsync(provider, "ذخیره");

        provider.WaitForAssertion(() => Assert.Contains("نام کمیته تکراری است.", provider.Markup, StringComparison.Ordinal));
    }
}
