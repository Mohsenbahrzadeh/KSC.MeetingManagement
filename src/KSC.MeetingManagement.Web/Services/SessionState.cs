using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KSC.MeetingManagement.Application.Identity;

namespace KSC.MeetingManagement.Web.Services;

public sealed record AuthTokensDto(string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc, string RefreshToken, DateTimeOffset RefreshTokenExpiresAtUtc, UserProfile User);

/// <summary>Outcome of a call to the auth endpoints. <see cref="Status"/> is null when the API could not be reached.</summary>
public sealed record AuthCallResult(AuthTokensDto? Tokens, HttpStatusCode? Status);

/// <summary>Calls the sign-in endpoints. Uses a client without the bearer handler, since these calls create the session.</summary>
public sealed class AuthApiClient(IHttpClientFactory httpClientFactory)
{
    public const string ClientName = "api-auth";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<AuthCallResult> LoginAsync(string email, string password, CancellationToken cancellationToken) =>
        PostAsync("api/v1/auth/login", new { email, password }, cancellationToken);

    public Task<AuthCallResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        PostAsync("api/v1/auth/refresh", new { refreshToken }, cancellationToken);

    public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClientFactory.CreateClient(ClientName)
                .PostAsJsonAsync(new Uri("api/v1/auth/logout", UriKind.Relative), new { refreshToken }, Json, cancellationToken);
        }
        catch (HttpRequestException)
        {
            // Signing out locally still works when the server cannot be reached.
        }
    }

    private async Task<AuthCallResult> PostAsync(string path, object body, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClientFactory.CreateClient(ClientName).PostAsJsonAsync(new Uri(path, UriKind.Relative), body, Json, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new AuthCallResult(null, response.StatusCode);
            }

            var tokens = await response.Content.ReadFromJsonAsync<AuthTokensDto>(Json, cancellationToken);
            return new AuthCallResult(tokens, tokens is null ? HttpStatusCode.BadGateway : response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new AuthCallResult(null, null);
        }
    }
}

public sealed record StoredSession(string RefreshToken, Guid? TenantId);

/// <summary>Keeps the refresh token in the browser tab so a page reload does not sign the user out.</summary>
public interface ISessionStore
{
    Task<StoredSession?> LoadAsync();

    Task SaveAsync(StoredSession session);

    Task ClearAsync();
}

/// <summary>
/// Who is signed in and which organisation they work in, for one browser circuit. The access token lives only in memory;
/// the refresh token is kept (encrypted) in session storage of the tab and replaced on every refresh.
/// </summary>
public sealed class SessionState(AuthApiClient api, ISessionStore store, TimeProvider clock) : IDisposable
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessExpires;
    private string? _refreshToken;

    public event Action? Changed;

    public bool Initialized { get; private set; }

    public UserProfile? User { get; private set; }

    public Guid? TenantId { get; private set; }

    public bool IsSignedIn => User is not null && _accessToken is not null;

    public string? AccessToken => _accessToken;

    public MembershipSummary? CurrentTenant => User?.Memberships.FirstOrDefault(m => m.TenantId == TenantId);

    /// <summary>Signs in. Returns a user-presentable error, or null on success.</summary>
    public async Task<string?> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var result = await api.LoginAsync(email, password, cancellationToken);
        if (result.Tokens is { } tokens)
        {
            await ApplyAsync(tokens, preferred: null);
            return null;
        }

        return result.Status switch
        {
            HttpStatusCode.Unauthorized => "ایمیل یا گذرواژه نادرست است.",
            HttpStatusCode.TooManyRequests => "تلاش‌های ناموفق زیاد بود. چند دقیقه بعد دوباره تلاش کنید.",
            HttpStatusCode.ServiceUnavailable => "ورود هنوز روی این سرور پیکربندی نشده است.",
            _ => ApiResultReader.GenericFailure,
        };
    }

    /// <summary>Called once per circuit after the first render: restores the session from the tab storage, if any.</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (Initialized)
        {
            return;
        }

        try
        {
            if (await store.LoadAsync() is { } saved)
            {
                var result = await api.RefreshAsync(saved.RefreshToken, cancellationToken);
                if (result.Tokens is { } tokens)
                {
                    await ApplyAsync(tokens, saved.TenantId);
                }
                else if (result.Status is not null)
                {
                    await store.ClearAsync();
                }
            }
        }
        finally
        {
            Initialized = true;
            Changed?.Invoke();
        }
    }

    /// <summary>Gets a new access token shortly before the current one expires. Ends the session when the refresh token is no longer valid.</summary>
    public async Task EnsureFreshAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSignedIn || _refreshToken is null || _accessExpires - clock.GetUtcNow() > RefreshMargin)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsSignedIn || _refreshToken is null || _accessExpires - clock.GetUtcNow() > RefreshMargin)
            {
                return;
            }

            var result = await api.RefreshAsync(_refreshToken, cancellationToken);
            if (result.Tokens is { } tokens)
            {
                await ApplyAsync(tokens, TenantId);
            }
            else if (result.Status is not null)
            {
                await ClearAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var token = _refreshToken;
        await ClearAsync();
        if (token is not null)
        {
            await api.LogoutAsync(token, cancellationToken);
        }
    }

    public async Task SelectTenantAsync(Guid tenantId)
    {
        if (User?.Memberships.Any(m => m.TenantId == tenantId) == true && _refreshToken is not null)
        {
            TenantId = tenantId;
            await SaveAsync();
            Changed?.Invoke();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task ApplyAsync(AuthTokensDto tokens, Guid? preferred)
    {
        _accessToken = tokens.AccessToken;
        _accessExpires = tokens.AccessTokenExpiresAtUtc;
        _refreshToken = tokens.RefreshToken;
        User = tokens.User;
        var memberships = tokens.User.Memberships;
        var wanted = preferred ?? TenantId;
        TenantId = memberships.Any(m => m.TenantId == wanted)
            ? wanted
            : memberships.Count > 0 ? memberships[0].TenantId : null;
        await SaveAsync();
        Changed?.Invoke();
    }

    private async Task ClearAsync()
    {
        _accessToken = null;
        _refreshToken = null;
        User = null;
        TenantId = null;
        try
        {
            await store.ClearAsync();
        }
        catch (InvalidOperationException)
        {
            // Storage is unavailable (for example while pre-rendering): nothing to clear.
        }

        Changed?.Invoke();
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_refreshToken is not null)
            {
                await store.SaveAsync(new StoredSession(_refreshToken, TenantId));
            }
        }
        catch (InvalidOperationException)
        {
            // Storage is unavailable: the session still works until the circuit ends.
        }
    }
}
