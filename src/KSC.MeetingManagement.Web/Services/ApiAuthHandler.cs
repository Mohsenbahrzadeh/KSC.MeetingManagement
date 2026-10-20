using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;

namespace KSC.MeetingManagement.Web.Services;

/// <summary>
/// Lets code that runs outside the circuit DI scope (such as HttpClient handlers) reach the services of the current circuit.
/// This is the pattern from the ASP.NET Core Blazor documentation.
/// </summary>
public sealed class CircuitServicesAccessor
{
    private static readonly AsyncLocal<IServiceProvider?> Current = new();

    public IServiceProvider? Services
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}

public sealed class ServicesAccessorCircuitHandler(IServiceProvider services, CircuitServicesAccessor accessor) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            accessor.Services = services;
            try
            {
                await next(context);
            }
            finally
            {
                accessor.Services = null;
            }
        };
}

/// <summary>Adds the access token and chosen organisation of the signed-in user to every API request.</summary>
public sealed class ApiAuthHandler(CircuitServicesAccessor accessor) : DelegatingHandler
{
    public const string TenantHeader = "X-Tenant-Id";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (accessor.Services?.GetService(typeof(SessionState)) is SessionState session)
        {
            await session.EnsureFreshAsync(cancellationToken);
            if (session.IsSignedIn)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
                request.Headers.Remove(TenantHeader);
                if (session.TenantId is { } tenant)
                {
                    request.Headers.TryAddWithoutValidation(TenantHeader, tenant.ToString());
                }
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Session storage of the browser tab, encrypted by ASP.NET Core data protection.</summary>
public sealed class ProtectedSessionStore(ProtectedSessionStorage storage) : ISessionStore
{
    private const string Key = "ksc.session";

    public async Task<StoredSession?> LoadAsync()
    {
        try
        {
            var result = await storage.GetAsync<StoredSession>(Key);
            return result.Success ? result.Value : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or CryptographicException or JSException)
        {
            return null;
        }
    }

    public async Task SaveAsync(StoredSession session)
    {
        try
        {
            await storage.SetAsync(Key, session);
        }
        catch (JSException)
        {
            // Browser storage is blocked: the user stays signed in until the tab is closed.
        }
    }

    public async Task ClearAsync()
    {
        try
        {
            await storage.DeleteAsync(Key);
        }
        catch (JSException)
        {
            // Nothing stored.
        }
    }
}
