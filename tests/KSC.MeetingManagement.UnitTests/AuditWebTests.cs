using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using KSC.MeetingManagement.Application.Audit;
using KSC.MeetingManagement.Domain.Audit;
using KSC.MeetingManagement.Web.Components.Pages;
using KSC.MeetingManagement.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace KSC.MeetingManagement.UnitTests;

public sealed class AuditWebTests : BunitContext, IAsyncLifetime
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ActorId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly List<string> _requests = [];
    private string[] _roles = ["Auditor"];
    private bool _forbidden;
    private int _count = 1;
    private bool _emptyNextPage;

    public AuditWebTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(new FakeFactory(new Handler(this)));
        Services.AddScoped<AuditApiClient>();
        Services.AddScoped<AuthApiClient>();
        Services.AddSingleton<ISessionStore>(new MemoryStore());
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        Services.AddScoped<SessionState>();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private sealed class Handler(AuditWebTests owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
                return Task.FromResult(Json(JsonSerializer.Serialize(new
                {
                    accessToken = "token",
                    accessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
                    refreshToken = "refresh",
                    refreshTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
                    user = new
                    {
                        id = ActorId,
                        email = "user@example.com",
                        displayName = "User",
                        memberships = new[] { new { tenantId = TenantId, tenantName = "Tenant", tenantSlug = "tenant", roles = owner._roles } },
                    },
                })));

            owner._requests.Add(request.RequestUri.PathAndQuery);
            if (owner._forbidden) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            var count = owner._emptyNextPage && request.RequestUri.Query.Contains("skip=50", StringComparison.Ordinal) ? 0 : owner._count;
            var items = Enumerable.Range(0, count).Select(_ => new
            {
                id = Guid.NewGuid(),
                occurredAtUtc = DateTimeOffset.UtcNow,
                actorType = "User",
                actorId = ActorId,
                action = "meeting.create",
                resourceType = "meeting",
                resourceId = Guid.NewGuid(),
                outcome = "Success",
                correlationId = "test-123",
                detail = (string?)null,
            });
            return Task.FromResult(Json(JsonSerializer.Serialize(items)));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private async Task<IRenderedComponent<AuditPage>> RenderPageAsync()
    {
        await Services.GetRequiredService<SessionState>().LoginAsync("user@example.com", "password", CancellationToken.None);
        Render<MudPopoverProvider>();
        return Render<AuditPage>();
    }

    [Fact]
    public async Task Auditor_can_view_events_and_search_but_invalid_id_does_not_request_api()
    {
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("meeting.create", page.Markup));
        Assert.Single(_requests);
        Assert.Contains("skip=0&take=50", _requests[0]);

        page.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "شناسهٔ عامل").Find("input").Change("not-a-guid");
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "جست‌وجو").Click());
        page.WaitForAssertion(() => Assert.Contains("شناسهٔ عامل و منبع", page.Markup));
        Assert.Single(_requests);
    }

    [Fact]
    public async Task Unauthorized_role_never_calls_audit_api()
    {
        _roles = ["Participant"];
        var page = await RenderPageAsync();
        Assert.Contains("دسترسی مشاهدهٔ دفتر ممیزی", page.Markup);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Forbidden_response_is_shown_without_events()
    {
        _forbidden = true;
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Contains("شما دسترسی لازم", page.Markup));
        Assert.DoesNotContain("meeting.create", page.Markup);
    }

    [Fact]
    public async Task Full_page_enables_next_page_with_skip()
    {
        _count = 50;
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Equal(50, page.FindAll("td[data-label='عملیات']").Count));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "بعدی").Click());
        page.WaitForAssertion(() => Assert.Contains(_requests, r => r.Contains("skip=50&take=50", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Empty_next_page_keeps_previous_button()
    {
        _count = 50;
        _emptyNextPage = true;
        var page = await RenderPageAsync();
        page.WaitForAssertion(() => Assert.Equal(50, page.FindAll("td[data-label='عملیات']").Count));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "بعدی").Click());
        page.WaitForAssertion(() => Assert.Contains("رویدادی یافت نشد", page.Markup));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "قبلی").Click());
        page.WaitForAssertion(() => Assert.Equal(50, page.FindAll("td[data-label='عملیات']").Count));
    }

    [Fact]
    public async Task Client_escapes_query_and_serializes_filters()
    {
        var query = new AuditQuery(null, null, "meeting.create & more", ActorId, null, AuditOutcome.Denied, 50, 50);
        var result = await Services.GetRequiredService<AuditApiClient>().SearchAsync(query, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Single(result.Value!);
        Assert.Contains("action=meeting.create%20%26%20more", _requests.Single());
        Assert.Contains($"actorId={ActorId}", _requests.Single());
        Assert.Contains("outcome=Denied", _requests.Single());
    }
}
