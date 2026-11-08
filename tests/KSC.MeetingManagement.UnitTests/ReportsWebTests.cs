using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using KSC.MeetingManagement.Domain.Meetings;
using KSC.MeetingManagement.Web.Components.Pages;
using KSC.MeetingManagement.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KSC.MeetingManagement.UnitTests;

public sealed class ReportsWebTests : BunitContext, IAsyncLifetime
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly List<string> _requests = [];
    private string[] _roles = ["Auditor"];
    private string _csvText = "Number,Title\r\n1,Planning\r\n";

    public ReportsWebTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(new FakeFactory(new Handler(this)));
        Services.AddScoped<ReportsApiClient>();
        Services.AddScoped<CommitteesApiClient>();
        Services.AddScoped<AuthApiClient>();
        Services.AddSingleton<ISessionStore>(new MemoryStore());
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        Services.AddScoped<SessionState>();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private sealed class Handler(ReportsWebTests owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/login", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(JsonSerializer.Serialize(new
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
                })));
            }

            owner._requests.Add(request.RequestUri.PathAndQuery);
            if (path.EndsWith("/committees", StringComparison.Ordinal)) return Task.FromResult(Json("[]"));
            if (path.EndsWith("/reports/meetings", StringComparison.Ordinal) && request.RequestUri.Query.Contains("format=csv", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(owner._csvText, Encoding.UTF8, "text/csv"),
                });
            }

            if (path.EndsWith("/reports/meetings", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"totalCount":1,"byStatus":{"Scheduled":1},"rows":[{"id":"33333333-3333-3333-3333-333333333333","number":"1","title":"Planning","status":"Scheduled","mode":"Online","startUtc":"2026-11-08T10:00:00Z","endUtc":"2026-11-08T11:00:00Z","organizerId":"22222222-2222-2222-2222-222222222222","committeeId":null}]}"""));
            if (path.EndsWith("/reports/attendance", StringComparison.Ordinal)) return Task.FromResult(Json("[]"));
            if (path.EndsWith("/reports/actions", StringComparison.Ordinal)) return Task.FromResult(Json("""{"total":0,"open":0,"inProgress":0,"done":0,"cancelled":0,"overdue":0,"byOwner":[]}"""));
            if (path.EndsWith("/reports/resolutions", StringComparison.Ordinal)) return Task.FromResult(Json("""{"total":0,"draft":0,"adopted":0,"rejected":0,"revoked":0}"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private async Task LoginAsync() =>
        await Services.GetRequiredService<SessionState>().LoginAsync("user@example.com", "password", CancellationToken.None);

    [Fact]
    public async Task Client_serializes_date_range_filters_and_pages_and_reads_report()
    {
        var client = Services.GetRequiredService<ReportsApiClient>();
        var from = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(3.5));
        var to = from.AddDays(30);
        var committeeId = Guid.NewGuid();

        var result = await client.MeetingsAsync(from, to, MeetingStatus.Scheduled, UserId, committeeId, 50, 50, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Value!.TotalCount);
        Assert.Equal(MeetingStatus.Scheduled, result.Value.Rows[0].Status);
        var request = Assert.Single(_requests);
        Assert.Contains("from=2026-11-01T00%3A00%3A00.0000000%2B03%3A30", request);
        Assert.Contains("to=2026-12-01T00%3A00%3A00.0000000%2B03%3A30", request);
        Assert.Contains("status=Scheduled", request);
        Assert.Contains($"organizerId={UserId}", request);
        Assert.Contains($"committeeId={committeeId}", request);
        Assert.Contains("skip=50&take=50", request);
    }

    [Fact]
    public async Task Csv_client_returns_csv_and_requests_full_export()
    {
        var client = Services.GetRequiredService<ReportsApiClient>();
        var from = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var result = await client.MeetingsCsvAsync(from, from.AddDays(1), null, null, null, 500, 500, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("Number,Title", result.Value);
        var request = Assert.Single(_requests);
        Assert.Contains("format=csv", request);
        Assert.Contains("skip=500&take=500", request);
    }

    [Fact]
    public async Task Csv_export_downloads_all_pages_and_only_keeps_one_header()
    {
        await LoginAsync();
        var page = Render<ReportsPage>();
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "اجرای گزارش‌ها").Click());
        page.WaitForAssertion(() => Assert.Contains("Planning", page.Markup));

        _csvText = "Number,Title\r\n1,First\r\n";
        JSInterop.SetupModule("./reports.js").SetupVoid("downloadText", "meetings.csv", "text/csv;charset=utf-8", "Number,Title\r\n1,First\r\n");
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "دریافت CSV").Click());
        page.WaitForAssertion(() => Assert.Contains(_requests, r => r.Contains("format=csv", StringComparison.Ordinal)));
        Assert.Contains(_requests, r => r.Contains("format=csv", StringComparison.Ordinal) && r.Contains("skip=0&take=500", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Page_hides_reports_and_makes_no_report_requests_without_permission()
    {
        _roles = ["Participant"];
        await LoginAsync();

        var page = Render<ReportsPage>();

        Assert.Contains("دسترسی مشاهدهٔ گزارش‌ها", page.Markup);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Authorized_page_loads_committee_filter_without_loading_reports_until_requested()
    {
        await LoginAsync();

        var page = Render<ReportsPage>();

        page.WaitForAssertion(() => Assert.Contains("اجرای گزارش‌ها", page.Markup));
        Assert.Single(_requests);
        Assert.EndsWith("/api/v1/committees", _requests[0]);
    }

    [Fact]
    public async Task Authorized_page_loads_all_report_sections_after_run()
    {
        await LoginAsync();
        var page = Render<ReportsPage>();
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "اجرای گزارش‌ها").Click());

        page.WaitForAssertion(() => Assert.Contains("Planning", page.Markup));
        Assert.Contains("گزارش جلسات", page.Markup);
        Assert.Contains("گزارش حضور و دعوت‌ها", page.Markup);
        Assert.Contains("گزارش اقدامات پیگیری", page.Markup);
        Assert.Contains("گزارش مصوبات", page.Markup);
        Assert.Contains(_requests, r => r.Contains("/reports/meetings?", StringComparison.Ordinal));
        Assert.Contains(_requests, r => r.Contains("/reports/attendance?", StringComparison.Ordinal));
        Assert.Contains(_requests, r => r.Contains("/reports/actions?", StringComparison.Ordinal));
        Assert.Contains(_requests, r => r.Contains("/reports/resolutions?", StringComparison.Ordinal));
    }
}
