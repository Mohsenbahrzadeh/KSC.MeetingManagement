using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Web.Components.Pages;
using KSC.MeetingManagement.Web.Components.Users;
using KSC.MeetingManagement.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KSC.MeetingManagement.UnitTests;

public sealed class UsersPageTests : BunitContext, IAsyncLifetime
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    private readonly List<(HttpMethod Method, string Path, string Body)> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

    public UsersPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(new Factory(this));
        Services.AddScoped<UsersApiClient>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private sealed class Factory(UsersPageTests owner) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(owner), disposeHandler: true) { BaseAddress = new Uri("http://api.test/") };
    }

    private sealed class Handler(UsersPageTests owner) : HttpMessageHandler
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

    private static string Member(string email, string name, string roles = "\"Participant\"", bool membership = true, bool user = true) =>
        $$"""{"userId":"{{Guid.NewGuid()}}","email":"{{email}}","displayName":"{{name}}","roles":[{{roles}}],"membershipActive":{{(membership ? "true" : "false")}},"userActive":{{(user ? "true" : "false")}}}""";

    private IRenderedComponent<UsersPage> RenderPage()
    {
        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        return Render<UsersPage>();
    }

    [Fact]
    public void Lists_members_with_persian_role_labels_and_active_state()
    {
        _respond = _ => Json($"[{Member("a@x.com", "علی", "\"TenantOwner\",\"Secretary\"")},{Member("b@x.com", "بابک", membership: false)}]");

        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tbody tr").Count));
        Assert.Contains("مالک سازمان، دبیر", cut.Markup);
        Assert.Contains("a@x.com", cut.Markup);
        Assert.Contains("غیرفعال", cut.Markup);
        Assert.Equal("/api/v1/users", _requests[0].Path);
    }

    [Fact]
    public void Shows_a_generic_error_and_recovers_on_retry()
    {
        _respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Contains("دسترسی لازم", cut.Markup));

        _respond = _ => Json($"[{Member("c@x.com", "کیان")}]");
        cut.Find("button.mud-button-text").Click();

        cut.WaitForAssertion(() => Assert.Contains("کیان", cut.Markup));
    }

    [Fact]
    public async Task Deactivating_a_member_sends_active_false_and_reloads()
    {
        var member = Member("d@x.com", "دانیال");
        var id = JsonSerializer.Deserialize<MemberDto>(member, ReadOptions)!.UserId;
        _respond = request => request.Method == HttpMethod.Put ? Json(member) : Json($"[{member}]");
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tbody tr")));

        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "غیرفعال کردن").Click());

        cut.WaitForAssertion(() => Assert.Contains(_requests, r => r.Method == HttpMethod.Put));
        var put = _requests.First(r => r.Method == HttpMethod.Put);
        Assert.Equal($"/api/v1/users/{id}/active", put.Path);
        Assert.False(JsonDocument.Parse(put.Body).RootElement.GetProperty("active").GetBoolean());
    }

    private async Task<IRenderedComponent<MudDialogProvider>> OpenDialogAsync(MemberDto? member)
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<MemberFormDialog> { { d => d.Member, member } };
        await provider.InvokeAsync(() => dialogs.ShowAsync<MemberFormDialog>("t", parameters));
        return provider;
    }

    private static Task ClickAsync(IRenderedComponent<MudDialogProvider> provider, string text) =>
        provider.InvokeAsync(() => provider.FindAll("button").Single(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click());

    [Fact]
    public async Task Adding_a_member_posts_email_name_password_and_the_default_role()
    {
        _respond = _ => Json(Member("new@x.com", "نو"), HttpStatusCode.Created);
        var provider = await OpenDialogAsync(null);

        provider.FindAll("input")[0].Input(" new@x.com ");
        provider.FindAll("input")[1].Change("نو");
        provider.FindAll("input")[2].Input("LongEnoughPass1");
        await ClickAsync(provider, "ذخیره");

        provider.WaitForAssertion(() => Assert.Single(_requests));
        var (method, path, body) = _requests[0];
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/api/v1/users", path);
        Assert.Equal("new@x.com", doc.RootElement.GetProperty("email").GetString());
        Assert.Equal("Participant", doc.RootElement.GetProperty("roles")[0].GetString());
    }

    [Fact]
    public async Task Invalid_input_is_explained_without_calling_the_api()
    {
        var provider = await OpenDialogAsync(null);

        await ClickAsync(provider, "ذخیره");

        provider.WaitForAssertion(() => Assert.Contains("ایمیل معتبر وارد کنید.", provider.Markup, StringComparison.Ordinal));
        Assert.Contains("رمز عبور باید حداقل", provider.Markup, StringComparison.Ordinal);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task A_duplicate_email_conflict_is_shown_in_persian()
    {
        _respond = _ => new HttpResponseMessage(HttpStatusCode.Conflict);
        var provider = await OpenDialogAsync(null);
        provider.FindAll("input")[0].Input("dup@x.com");
        provider.FindAll("input")[2].Input("LongEnoughPass1");

        await ClickAsync(provider, "ذخیره");

        provider.WaitForAssertion(() => Assert.Contains("قبلاً در این سازمان ثبت شده", provider.Markup, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("a@b.co", "LongEnoughPass1", true)]
    [InlineData("nobody", "LongEnoughPass1", false)]
    [InlineData("a@", "LongEnoughPass1", false)]
    [InlineData("a b@c.d", "LongEnoughPass1", false)]
    [InlineData("a@b.co", "short", false)]
    [InlineData("a@b.co", "a@b.co", false)]
    public void Validate_checks_email_and_password_for_new_members(string email, string password, bool valid)
    {
        var errors = MemberFormDialog.Validate(email, password, ["Participant"], isNew: true);

        Assert.Equal(valid, errors.Count == 0);
    }

    [Fact]
    public void Validate_requires_at_least_one_role_even_when_editing()
    {
        Assert.Contains("roles", MemberFormDialog.Validate(string.Empty, string.Empty, [], isNew: false).Keys);
    }
}
