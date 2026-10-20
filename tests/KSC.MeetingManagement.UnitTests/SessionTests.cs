using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bunit;
using KSC.MeetingManagement.Web.Components.Layout;
using KSC.MeetingManagement.Web.Components.Pages;
using KSC.MeetingManagement.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace KSC.MeetingManagement.UnitTests;

internal sealed class FakeAuthServer : HttpMessageHandler
{
    public static readonly Guid TenantA = Guid.NewGuid();
    public static readonly Guid TenantB = Guid.NewGuid();

    private static readonly string[] ParticipantRole = ["Participant"];

    public List<string> Calls { get; } = [];

    public Func<string, string?, HttpResponseMessage>? Override { get; set; }

    public int Issued { get; private set; }

    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    public int Tenants { get; set; } = 1;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add(path);
        if (Override?.Invoke(path, body) is { } custom)
        {
            return custom;
        }

        if (path.EndsWith("/logout", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return Tokens();
    }

    public HttpResponseMessage Tokens()
    {
        Issued++;
        var memberships = new List<object> { new { tenantId = TenantA, tenantName = "الف", tenantSlug = "a", roles = ParticipantRole } };
        if (Tenants > 1)
        {
            memberships.Add(new { tenantId = TenantB, tenantName = "ب", tenantSlug = "b", roles = ParticipantRole });
        }

        var json = JsonSerializer.Serialize(new
        {
            accessToken = $"access-{Issued}",
            accessTokenExpiresAtUtc = Now.AddMinutes(15),
            refreshToken = $"refresh-{Issued}",
            refreshTokenExpiresAtUtc = Now.AddDays(14),
            user = new { id = Guid.NewGuid(), email = "ali@example.com", displayName = "علی", memberships },
        });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    public static HttpResponseMessage Status(HttpStatusCode code) => new(code);
}

internal sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://api.test/") };
}

internal sealed class MemoryStore : ISessionStore
{
    public StoredSession? Saved { get; set; }

    public Task<StoredSession?> LoadAsync() => Task.FromResult(Saved);

    public Task SaveAsync(StoredSession session)
    {
        Saved = session;
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        Saved = null;
        return Task.CompletedTask;
    }
}

internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class SessionStateTests : IDisposable
{
    private readonly FakeAuthServer _server = new();
    private readonly MemoryStore _store = new();
    private readonly FakeClock _clock;

    public SessionStateTests() => _clock = new FakeClock(_server.Now);

    public void Dispose() => _server.Dispose();

    private SessionState Create() => new(new AuthApiClient(new FakeFactory(_server)), _store, _clock);

    [Fact]
    public async Task Login_signs_in_selects_the_only_organisation_and_remembers_the_refresh_token()
    {
        var session = Create();

        var error = await session.LoginAsync("ali@example.com", "pw", CancellationToken.None);

        Assert.Null(error);
        Assert.True(session.IsSignedIn);
        Assert.Equal(FakeAuthServer.TenantA, session.TenantId);
        Assert.Equal("refresh-1", _store.Saved?.RefreshToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "نادرست")]
    [InlineData(HttpStatusCode.TooManyRequests, "ناموفق")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "پیکربندی")]
    [InlineData(HttpStatusCode.InternalServerError, "دوباره")]
    public async Task Login_failures_give_a_persian_message_and_no_session(HttpStatusCode code, string expected)
    {
        _server.Override = (_, _) => FakeAuthServer.Status(code);
        var session = Create();

        var error = await session.LoginAsync("ali@example.com", "bad", CancellationToken.None);

        Assert.Contains(expected, error, StringComparison.Ordinal);
        Assert.False(session.IsSignedIn);
        Assert.Null(_store.Saved);
    }

    [Fact]
    public async Task Restore_signs_in_again_from_the_stored_refresh_token_and_keeps_the_chosen_organisation()
    {
        _server.Tenants = 2;
        _store.Saved = new StoredSession("old", FakeAuthServer.TenantB);
        var session = Create();

        await session.RestoreAsync();

        Assert.True(session.Initialized);
        Assert.True(session.IsSignedIn);
        Assert.Equal(FakeAuthServer.TenantB, session.TenantId);
        Assert.Equal("refresh-1", _store.Saved?.RefreshToken);
    }

    [Fact]
    public async Task Restore_forgets_a_rejected_token_but_keeps_it_when_the_server_is_unreachable()
    {
        _store.Saved = new StoredSession("old", null);
        _server.Override = (_, _) => FakeAuthServer.Status(HttpStatusCode.Unauthorized);
        var rejected = Create();
        await rejected.RestoreAsync();
        Assert.False(rejected.IsSignedIn);
        Assert.Null(_store.Saved);

        _store.Saved = new StoredSession("old", null);
        _server.Override = (_, _) => throw new HttpRequestException("down");
        var offline = Create();
        await offline.RestoreAsync();
        Assert.True(offline.Initialized);
        Assert.False(offline.IsSignedIn);
        Assert.NotNull(_store.Saved);
    }

    [Fact]
    public async Task The_access_token_is_renewed_shortly_before_it_expires_and_the_session_ends_if_renewal_is_refused()
    {
        var session = Create();
        await session.LoginAsync("ali@example.com", "pw", CancellationToken.None);

        await session.EnsureFreshAsync();
        Assert.Equal("access-1", session.AccessToken);

        _clock.Now = _server.Now.AddMinutes(14).AddSeconds(30);
        await session.EnsureFreshAsync();
        Assert.Equal("access-2", session.AccessToken);
        Assert.Equal("refresh-2", _store.Saved?.RefreshToken);

        _clock.Now = _clock.Now.AddMinutes(20);
        _server.Override = (_, _) => FakeAuthServer.Status(HttpStatusCode.Unauthorized);
        await session.EnsureFreshAsync();
        Assert.False(session.IsSignedIn);
        Assert.Null(_store.Saved);
    }

    [Fact]
    public async Task Only_an_organisation_the_user_belongs_to_can_be_selected()
    {
        _server.Tenants = 2;
        var session = Create();
        await session.LoginAsync("ali@example.com", "pw", CancellationToken.None);

        await session.SelectTenantAsync(FakeAuthServer.TenantB);
        Assert.Equal(FakeAuthServer.TenantB, session.TenantId);
        Assert.Equal(FakeAuthServer.TenantB, _store.Saved?.TenantId);

        await session.SelectTenantAsync(Guid.NewGuid());
        Assert.Equal(FakeAuthServer.TenantB, session.TenantId);
    }

    [Fact]
    public async Task Logout_clears_the_session_and_revokes_the_refresh_token_on_the_server()
    {
        var session = Create();
        await session.LoginAsync("ali@example.com", "pw", CancellationToken.None);

        await session.LogoutAsync();

        Assert.False(session.IsSignedIn);
        Assert.Null(_store.Saved);
        Assert.Contains("/api/v1/auth/logout", _server.Calls);
    }

    [Fact]
    public async Task The_handler_adds_token_and_organisation_only_for_a_signed_in_circuit()
    {
        var session = Create();
        await session.LoginAsync("ali@example.com", "pw", CancellationToken.None);
        var accessor = new CircuitServicesAccessor();
        var services = new ServiceCollection().AddSingleton(session).BuildServiceProvider();
        var captured = new List<HttpRequestMessage>();
        var inner = new CapturingHandler(captured);
        using var client = new HttpClient(new ApiAuthHandler(accessor) { InnerHandler = inner }) { BaseAddress = new Uri("http://api.test/") };

        await client.GetAsync(new Uri("x", UriKind.Relative));
        Assert.Null(captured[0].Headers.Authorization);

        accessor.Services = services;
        var request = new HttpRequestMessage(HttpMethod.Get, "x");
        request.Headers.Add("X-Tenant-Id", Guid.NewGuid().ToString());
        await client.SendAsync(request);

        Assert.Equal(new AuthenticationHeaderValue("Bearer", "access-1"), captured[1].Headers.Authorization);
        Assert.Equal(FakeAuthServer.TenantA.ToString(), Assert.Single(captured[1].Headers.GetValues("X-Tenant-Id")));
        accessor.Services = null;
    }

    private sealed class CapturingHandler(List<HttpRequestMessage> sink) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sink.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/calendar", "/calendar")]
    [InlineData("/meetings?x=1", "/meetings?x=1")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    public void The_return_address_must_be_a_local_path(string? input, string expected) =>
        Assert.Equal(expected, Login.SafeTarget(input));
}

public sealed class SessionLayoutTests : BunitContext, IAsyncLifetime
{
    private readonly FakeAuthServer _server = new();
    private readonly MemoryStore _store = new();

    public SessionLayoutTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<SessionState>(_ => new SessionState(new AuthApiClient(new FakeFactory(_server)), _store, new FakeClock(_server.Now)));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private static RenderFragment Body() => b => b.AddMarkupContent(0, "<p id=\"body\">محتوا</p>");

    [Fact]
    public void Anonymous_visitors_are_sent_to_the_login_page_with_the_page_they_wanted_and_see_no_menu()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/meetings");

        var cut = Render<MainLayout>(p => p.Add(c => c.Body, Body()));

        cut.WaitForAssertion(() => Assert.Contains("/login?returnUrl=%2Fmeetings", navigation.Uri, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("a.mud-nav-link"));
    }

    [Fact]
    public void The_login_page_itself_is_reachable_without_a_session()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/login");

        var cut = Render<MainLayout>(p => p.Add(c => c.Body, Body()));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#body")));
    }

    [Fact]
    public void A_stored_session_is_restored_and_the_menu_and_page_are_shown()
    {
        _store.Saved = new StoredSession("old", null);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/meetings");

        var cut = Render<MainLayout>(p => p.Add(c => c.Body, Body()));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#body")));
        Assert.Equal(NavItems.Main.Select(i => i.Href), cut.FindAll("a.mud-nav-link").Select(a => a.GetAttribute("href")));
    }
}
