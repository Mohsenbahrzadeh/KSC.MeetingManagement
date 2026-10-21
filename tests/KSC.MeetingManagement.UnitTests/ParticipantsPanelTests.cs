using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using KSC.MeetingManagement.Web.Components.Meetings;
using KSC.MeetingManagement.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KSC.MeetingManagement.UnitTests;

public sealed class ParticipantsPanelTests : BunitContext, IAsyncLifetime
{
    private static readonly Guid MeetingId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AliId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid BabakId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly List<(HttpMethod Method, string Path, string Body)> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

    public ParticipantsPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(new Factory(this));
        Services.AddScoped<ParticipantsApiClient>();
        Services.AddScoped<UsersApiClient>();
        Services.AddSingleton(new FixedClock(new DateTimeOffset(2025, 3, 21, 0, 0, 0, TimeSpan.Zero)));
        Services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FixedClock>());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Factory(ParticipantsPanelTests owner) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(owner), disposeHandler: true) { BaseAddress = new Uri("http://api.test/") };
    }

    private sealed class Handler(ParticipantsPanelTests owner) : HttpMessageHandler
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

    private static string Internal(Guid userId, string status = "Accepted") =>
        $$"""{"id":"{{Guid.NewGuid()}}","meetingId":"{{MeetingId}}","kind":"Internal","userId":"{{userId}}","status":"{{status}}"}""";

    private static string Guest(string name, string status = "Invited") =>
        $$"""{"id":"{{Guid.NewGuid()}}","meetingId":"{{MeetingId}}","kind":"Guest","guestName":"{{name}}","guestEmail":"g@x.com","status":"{{status}}"}""";

    private static string Member(Guid id, string name, bool active = true) =>
        $$"""{"userId":"{{id}}","email":"{{name}}@x.com","displayName":"{{name}}","roles":["Participant"],"membershipActive":{{(active ? "true" : "false")}},"userActive":true}""";

    private static HttpResponseMessage Route(HttpRequestMessage r, string participants, string? users = null) =>
        r.RequestUri!.AbsolutePath.EndsWith("/users", StringComparison.Ordinal)
            ? users is null ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Json(users)
            : Json(participants);

    private (IRenderedComponent<ParticipantsPanel> Panel, IRenderedComponent<MudDialogProvider> Dialogs) RenderPanel(bool canEdit = true)
    {
        Render<MudPopoverProvider>();
        var dialogs = Render<MudDialogProvider>();
        return (Render<ParticipantsPanel>(p => p.Add(c => c.MeetingId, MeetingId).Add(c => c.CanEdit, canEdit)), dialogs);
    }

    [Fact]
    public void Lists_members_by_name_when_known_and_guests_by_their_name()
    {
        _respond = r => Route(r, $"[{Internal(AliId)},{Guest("مهمان یک", "Declined")}]", $"[{Member(AliId, "علی")}]");

        var (panel, _) = RenderPanel();

        panel.WaitForAssertion(() => Assert.Equal(2, panel.FindAll("tbody tr").Count));
        Assert.Contains("علی", panel.Markup);
        Assert.Contains("مهمان یک", panel.Markup);
        Assert.Contains("پذیرفته", panel.Markup);
        Assert.Contains("رد کرده", panel.Markup);
        Assert.Contains("مهمان", panel.Markup);
    }

    [Fact]
    public void Members_without_a_known_name_get_a_generic_label_when_the_user_list_is_forbidden()
    {
        _respond = r => Route(r, $"[{Internal(AliId)}]");

        var (panel, _) = RenderPanel();

        panel.WaitForAssertion(() => Assert.Contains("عضو سازمان", panel.Find("tbody").TextContent));
    }

    [Fact]
    public void Read_only_when_the_meeting_is_closed()
    {
        _respond = r => Route(r, $"[{Internal(AliId)}]");

        var (panel, _) = RenderPanel(canEdit: false);

        panel.WaitForAssertion(() => Assert.Single(panel.FindAll("tbody tr")));
        Assert.DoesNotContain(panel.FindAll("button"), b => b.TextContent.Contains("افزودن عضو", StringComparison.Ordinal) || b.TextContent.Contains("لغو دعوت", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_state_and_error_with_retry()
    {
        _respond = r => Route(r, "[]");
        var (empty, _) = RenderPanel();
        empty.WaitForAssertion(() => Assert.Contains("هنوز شرکت‌کننده‌ای اضافه نشده است.", empty.Markup));
    }

    [Fact]
    public void A_failed_load_can_be_retried()
    {
        _respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var (panel, _) = RenderPanel();
        panel.WaitForAssertion(() => Assert.Contains("عملیات انجام نشد", panel.Markup));

        _respond = r => Route(r, $"[{Guest("بازیابی")}]");
        panel.Find("button.mud-button-text").Click();

        panel.WaitForAssertion(() => Assert.Contains("بازیابی", panel.Markup));
    }

    [Fact]
    public async Task Revoking_asks_first_then_posts_to_the_participant()
    {
        var guest = Guest("مهمان");
        var id = JsonDocument.Parse(guest).RootElement.GetProperty("id").GetGuid();
        _respond = r => r.Method == HttpMethod.Post ? Json(guest) : Route(r, $"[{guest}]");
        var (panel, dialogs) = RenderPanel();
        panel.WaitForAssertion(() => Assert.Single(panel.FindAll("tbody tr")));

        await panel.InvokeAsync(() => panel.FindAll("button").Single(b => b.TextContent.Trim() == "لغو دعوت").Click());
        dialogs.WaitForAssertion(() => Assert.Contains("لغو شود؟", dialogs.Markup));
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Post);
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "بله").Click());

        panel.WaitForAssertion(() => Assert.Contains(_requests, r => r.Method == HttpMethod.Post));
        Assert.Equal($"/api/v1/meetings/{MeetingId}/participants/{id}/revoke", _requests.First(r => r.Method == HttpMethod.Post).Path);
    }

    private async Task<IRenderedComponent<MudDialogProvider>> OpenAddAsync(IReadOnlyCollection<Guid> excluded)
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<AddParticipantDialog> { { d => d.MeetingId, MeetingId }, { d => d.ExcludedUserIds, excluded } };
        await provider.InvokeAsync(() => dialogs.ShowAsync<AddParticipantDialog>("t", parameters));
        return provider;
    }

    [Fact]
    public async Task Add_dialog_without_a_selection_explains_and_sends_nothing()
    {
        _respond = r => Route(r, "[]", $"[{Member(AliId, "علی")}]");
        var provider = await OpenAddAsync([]);
        provider.WaitForAssertion(() => Assert.Contains("عضو سازمان", provider.Markup, StringComparison.Ordinal));

        await provider.InvokeAsync(() => provider.FindAll("button").Single(b => b.TextContent.Trim() == "افزودن").Click());

        provider.WaitForAssertion(() => Assert.Contains("یک عضو را انتخاب کنید.", provider.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public void Candidates_exclude_inactive_and_already_added_members()
    {
        var carolId = Guid.NewGuid();
        var members = new[] { Member(AliId, "علی"), Member(BabakId, "بابک"), Member(carolId, "کارول", active: false) }
            .Select(j => JsonSerializer.Deserialize<KSC.MeetingManagement.Application.Identity.MemberDto>(j, WebJson)!)
            .ToList();

        var candidates = AddParticipantDialog.Candidates(members, [AliId]);

        Assert.Equal([BabakId], candidates.Select(m => m.UserId));
    }

    [Fact]
    public async Task Add_dialog_explains_that_the_member_list_needs_the_users_permission()
    {
        _respond = r => Route(r, "[]");
        var provider = await OpenAddAsync([]);

        provider.WaitForAssertion(() => Assert.Contains("فقط برای مدیر کاربران", provider.Markup, StringComparison.Ordinal));
    }

    private async Task<IRenderedComponent<MudDialogProvider>> OpenInviteAsync()
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<InviteGuestDialog> { { d => d.MeetingId, MeetingId } };
        await provider.InvokeAsync(() => dialogs.ShowAsync<InviteGuestDialog>("t", parameters));
        return provider;
    }

    [Fact]
    public async Task Inviting_a_guest_posts_the_expiry_and_shows_the_one_time_token()
    {
        _respond = _ => Json($$"""{"participant":{{Guest("مهمان جدید")}},"token":"TOKEN-ONCE-123"}""", HttpStatusCode.Created);
        var provider = await OpenInviteAsync();

        provider.FindAll("input")[0].Input("مهمان جدید");
        provider.FindAll("input")[1].Input(" g@x.com ");
        await provider.InvokeAsync(() => provider.FindAll("button").Single(b => b.TextContent.Trim() == "دعوت").Click());

        provider.WaitForAssertion(() => Assert.Contains("این کد فقط همین یک بار نمایش داده می‌شود.", provider.Markup, StringComparison.Ordinal));
        Assert.Contains("TOKEN-ONCE-123", provider.Markup, StringComparison.Ordinal);
        var (method, path, body) = Assert.Single(_requests);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal($"/api/v1/meetings/{MeetingId}/participants/guests", path);
        Assert.Equal("g@x.com", doc.RootElement.GetProperty("email").GetString());
        Assert.Equal(new DateTimeOffset(2025, 3, 28, 0, 0, 0, TimeSpan.Zero), doc.RootElement.GetProperty("expiresUtc").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("مهمان", "g@x.com", 7, true)]
    [InlineData("", "g@x.com", 7, false)]
    [InlineData("مهمان", "bad", 7, false)]
    [InlineData("مهمان", "g@x.com", 0, false)]
    [InlineData("مهمان", "g@x.com", 91, false)]
    public void Guest_validation_checks_name_email_and_validity(string name, string email, int days, bool valid)
    {
        Assert.Equal(valid, InviteGuestDialog.Validate(name, email, days).Count == 0);
    }
}
