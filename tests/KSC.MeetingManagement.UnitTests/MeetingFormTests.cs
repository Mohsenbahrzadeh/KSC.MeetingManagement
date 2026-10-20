using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using KSC.MeetingManagement.Calendar;
using KSC.MeetingManagement.Domain.Meetings;
using KSC.MeetingManagement.Web.Components.Common;
using KSC.MeetingManagement.Web.Components.Meetings;
using KSC.MeetingManagement.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KSC.MeetingManagement.UnitTests;

public class MeetingFormModelTests
{
    private static readonly TimeZoneInfo Tehran = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(3.5), "Tehran", "Tehran");
    private static readonly CalendarLayout Layout = new(Tehran);

    private static MeetingFormModel Valid() => new()
    {
        Title = "هیئت مدیره",
        Date = JalaliDate.Create(1404, 1, 1),
        StartTime = TimeSpan.FromHours(9),
        EndTime = TimeSpan.FromHours(10.5),
    };

    [Fact]
    public void A_complete_form_is_valid_and_converts_local_jalali_time_to_utc()
    {
        var model = Valid();

        Assert.Empty(model.Validate());
        var slot = model.ToSlot(Layout);

        Assert.Equal(new DateTimeOffset(2025, 3, 21, 5, 30, 0, TimeSpan.Zero), slot.StartUtc);
        Assert.Equal(new DateTimeOffset(2025, 3, 21, 7, 0, 0, TimeSpan.Zero), slot.EndUtc);
    }

    [Fact]
    public void A_slot_round_trips_through_the_form_without_shifting()
    {
        var slot = Layout.SlotAt(JalaliDate.Create(1404, 6, 31), new TimeOnly(22, 0), TimeSpan.FromHours(1));

        var model = MeetingFormModel.FromSlot(slot, Layout);
        model.Title = "x";

        Assert.Equal(JalaliDate.Create(1404, 6, 31), model.Date);
        Assert.Equal(TimeSpan.FromHours(22), model.StartTime);
        Assert.Equal(slot, model.ToSlot(Layout));
    }

    [Fact]
    public void A_slot_running_past_midnight_is_clamped_to_the_end_of_that_day()
    {
        var slot = Layout.SlotAt(JalaliDate.Create(1404, 6, 31), new TimeOnly(23, 0), TimeSpan.FromHours(1));

        var model = MeetingFormModel.FromSlot(slot, Layout);
        model.Title = "x";

        Assert.Empty(model.Validate());
        Assert.Equal(new TimeSpan(23, 59, 0), model.EndTime);
        Assert.Equal(JalaliDate.Create(1404, 6, 31), model.Date);
    }

    [Fact]
    public void An_existing_meeting_is_shown_in_local_time()
    {
        var model = MeetingFormModel.FromMeeting(
            "x", null, MeetingMode.Online,
            new DateTimeOffset(2025, 3, 20, 20, 30, 0, TimeSpan.Zero), new DateTimeOffset(2025, 3, 20, 21, 30, 0, TimeSpan.Zero), Layout);

        Assert.Equal(JalaliDate.Create(1404, 1, 1), model.Date);
        Assert.Equal(TimeSpan.Zero, model.StartTime);
        Assert.Equal(TimeSpan.FromHours(1), model.EndTime);
    }

    [Fact]
    public void Missing_fields_are_reported_per_field()
    {
        var errors = new MeetingFormModel { Title = "  " }.Validate();

        Assert.Equal(["Title", "Date", "StartTime", "EndTime"], errors.Keys);
    }

    [Fact]
    public void End_must_be_after_start_and_title_length_is_limited()
    {
        var model = Valid();
        model.EndTime = model.StartTime;
        model.Title = new string('a', MeetingFormModel.MaxTitleLength + 1);

        var errors = model.Validate();

        Assert.Contains("EndTime", errors.Keys);
        Assert.Contains("Title", errors.Keys);
        Assert.Throws<InvalidOperationException>(() => model.ToSlot(Layout));
    }
}

public sealed class MeetingFormDialogTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeZoneInfo Tehran = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(3.5), "Tehran", "Tehran");
    private static readonly CalendarLayout Layout = new(Tehran);

    private readonly List<(HttpMethod Method, string Path, string Body)> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

    public MeetingFormDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(new Factory(this));
        Services.AddScoped<MeetingsApiClient>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private sealed class Factory(MeetingFormDialogTests owner) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(owner), disposeHandler: true) { BaseAddress = new Uri("http://api.test/") };
    }

    private sealed class Handler(MeetingFormDialogTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            owner._requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return owner._respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, mediaType) };

    private static string MeetingJson(string title = "جلسه", long version = 1) =>
        $$"""{"id":"{{Guid.NewGuid()}}","title":"{{title}}","status":"Scheduled","mode":"Online","organizerId":"{{Guid.NewGuid()}}","version":{{version}},"startUtc":"2025-03-21T05:30:00+00:00","endUtc":"2025-03-21T06:30:00+00:00","number":"2025-0001"}""";

    private async Task<IRenderedComponent<MudDialogProvider>> OpenAsync(CalendarSlot? slot, Guid? meetingId = null)
    {
        Render<MudPopoverProvider>();
        var provider = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<MeetingFormDialog> { { d => d.Layout, Layout }, { d => d.Slot, slot }, { d => d.MeetingId, meetingId } };
        await provider.InvokeAsync(() => dialogs.ShowAsync<MeetingFormDialog>("t", parameters));
        return provider;
    }

    private static CalendarSlot NineToTen => Layout.SlotAt(JalaliDate.Create(1404, 1, 1), new TimeOnly(9, 0), TimeSpan.FromHours(1));

    private static void ClickSave(IRenderedComponent<MudDialogProvider> provider) =>
        provider.FindAll("button").Single(b => b.TextContent.Contains("ذخیره", StringComparison.Ordinal)).Click();

    [Fact]
    public async Task Create_from_a_slot_posts_the_exact_utc_times_with_a_client_generated_id()
    {
        _respond = _ => Json(HttpStatusCode.Created, MeetingJson());
        var provider = await OpenAsync(NineToTen);

        provider.FindAll("input")[0].Input("هیئت مدیره");
        ClickSave(provider);

        provider.WaitForAssertion(() => Assert.Single(_requests));
        var (method, path, body) = _requests[0];
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/api/v1/meetings", path);
        Assert.Equal("هیئت مدیره", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(new DateTimeOffset(2025, 3, 21, 5, 30, 0, TimeSpan.Zero), doc.RootElement.GetProperty("start").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2025, 3, 21, 6, 30, 0, TimeSpan.Zero), doc.RootElement.GetProperty("end").GetDateTimeOffset());
        Assert.NotEqual(Guid.Empty, doc.RootElement.GetProperty("id").GetGuid());
        Assert.False(doc.RootElement.GetProperty("allowConflicts").GetBoolean());
    }

    [Fact]
    public async Task Invalid_form_shows_errors_and_sends_nothing()
    {
        var provider = await OpenAsync(NineToTen);

        ClickSave(provider);

        provider.WaitForAssertion(() => Assert.Contains("عنوان جلسه الزامی است", provider.Markup, StringComparison.Ordinal));
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task A_time_conflict_is_explained_and_can_be_forced_explicitly()
    {
        var conflict = """{"title":"تداخل زمانی","status":409,"detail":"این زمان با جلسه «هیئت» تداخل دارد.","conflicts":[{"meetingId":"00000000-0000-0000-0000-000000000001"}]}""";
        _respond = _ => _requests.Count == 1
            ? Json(HttpStatusCode.Conflict, conflict, "application/problem+json")
            : Json(HttpStatusCode.Created, MeetingJson());
        var provider = await OpenAsync(NineToTen);
        provider.FindAll("input")[0].Input("جلسه");

        ClickSave(provider);
        provider.WaitForAssertion(() => Assert.Contains("تداخل دارد", provider.Markup, StringComparison.Ordinal));
        provider.FindAll("button").Single(b => b.TextContent.Contains("ثبت با وجود تداخل", StringComparison.Ordinal)).Click();

        provider.WaitForAssertion(() => Assert.Equal(2, _requests.Count));
        using var doc = JsonDocument.Parse(_requests[1].Body);
        Assert.True(doc.RootElement.GetProperty("allowConflicts").GetBoolean());
    }

    [Fact]
    public async Task Server_failures_show_a_generic_message_without_internals()
    {
        _respond = _ => Json(HttpStatusCode.InternalServerError, """{"title":"خطای داخلی سرور","detail":"password=secret"}""");
        var provider = await OpenAsync(NineToTen);
        provider.FindAll("input")[0].Input("جلسه");

        ClickSave(provider);

        provider.WaitForAssertion(() => Assert.Contains("عملیات انجام نشد", provider.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("secret", provider.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_loads_the_meeting_then_updates_with_its_version()
    {
        var id = Guid.NewGuid();
        _respond = r => r.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, MeetingJson("قدیمی", 7)) : Json(HttpStatusCode.OK, MeetingJson("جدید", 8));
        var provider = await OpenAsync(null, id);

        provider.WaitForAssertion(() => Assert.Equal("قدیمی", provider.FindAll("input")[0].GetAttribute("value")));
        provider.FindAll("input")[0].Input("جدید");
        ClickSave(provider);

        provider.WaitForAssertion(() => Assert.Equal(2, _requests.Count));
        Assert.Equal(HttpMethod.Put, _requests[1].Method);
        using var doc = JsonDocument.Parse(_requests[1].Body);
        Assert.Equal(7, doc.RootElement.GetProperty("expectedVersion").GetInt64());
    }

    [Fact]
    public async Task A_failed_load_shows_the_error_state_with_retry()
    {
        _respond = _ => Json(HttpStatusCode.InternalServerError, "{}");
        var provider = await OpenAsync(null, Guid.NewGuid());

        provider.WaitForAssertion(() => Assert.Contains("تلاش دوباره", provider.Markup, StringComparison.Ordinal));
        Assert.Contains(provider.FindAll("button"), b => b.TextContent.Contains("ذخیره", StringComparison.Ordinal) && b.HasAttribute("disabled"));
    }

    [Fact]
    public void State_panel_covers_loading_empty_error_and_ready()
    {
        Render<MudPopoverProvider>();
        var loading = Render<StatePanel>(p => p.Add(c => c.State, PanelState.Loading));
        var empty = Render<StatePanel>(p => p.Add(c => c.State, PanelState.Empty).Add(c => c.EmptyText, "هیچ جلسه‌ای نیست"));
        var error = Render<StatePanel>(p => p.Add(c => c.State, PanelState.Error).Add(c => c.ErrorText, "خطا"));
        var ready = Render<StatePanel>(p => p.Add(c => c.State, PanelState.Ready).AddChildContent("<p id=\"ok\">محتوا</p>"));

        Assert.Contains("در حال بارگیری", loading.Markup, StringComparison.Ordinal);
        Assert.Equal("status", loading.Find("[role=status]").GetAttribute("role"));
        Assert.Contains("هیچ جلسه‌ای نیست", empty.Markup, StringComparison.Ordinal);
        Assert.Contains("خطا", error.Markup, StringComparison.Ordinal);
        Assert.Equal("محتوا", ready.Find("#ok").TextContent);
    }
}

public class ApiResultReaderTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static HttpResponseMessage Response(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"detail":"عنوان لازم است"}""", ApiErrorKind.Validation, "عنوان لازم است")]
    [InlineData(HttpStatusCode.Conflict, """{"detail":"تداخل","conflicts":[]}""", ApiErrorKind.TimeConflict, "تداخل")]
    [InlineData(HttpStatusCode.Conflict, """{"detail":"concurrency internals"}""", ApiErrorKind.VersionConflict, null)]
    [InlineData(HttpStatusCode.Forbidden, "{}", ApiErrorKind.Forbidden, null)]
    [InlineData(HttpStatusCode.NotFound, "", ApiErrorKind.NotFound, null)]
    [InlineData(HttpStatusCode.InternalServerError, """{"detail":"stack trace"}""", ApiErrorKind.Failure, null)]
    public async Task Status_codes_map_to_safe_messages(HttpStatusCode code, string body, ApiErrorKind kind, string? expectedMessage)
    {
        var result = await ApiResultReader.ReadAsync<MeetingInputDto>(Response(code, body), Options, CancellationToken.None);

        Assert.Equal(kind, result.Error);
        Assert.False(result.Succeeded);
        Assert.DoesNotContain("internals", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("stack trace", result.Message, StringComparison.Ordinal);
        if (expectedMessage is not null)
        {
            Assert.Equal(expectedMessage, result.Message);
        }
    }

    [Fact]
    public async Task Success_deserialises_and_garbage_success_is_a_failure()
    {
        var ok = await ApiResultReader.ReadAsync<MeetingInputDto>(Response(HttpStatusCode.OK, """{"name":"x"}"""), Options, CancellationToken.None);
        var bad = await ApiResultReader.ReadAsync<MeetingInputDto>(Response(HttpStatusCode.OK, "not json"), Options, CancellationToken.None);

        Assert.True(ok.Succeeded);
        Assert.Equal("x", ok.Value!.Name);
        Assert.Equal(ApiErrorKind.Failure, bad.Error);
    }

    private sealed record MeetingInputDto(string Name);
}
