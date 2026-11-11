using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Routing;

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

/// <summary>Process-local active Blazor circuit count. Prometheus scrapes the Web process separately from the API.</summary>
public sealed class CircuitMetrics
{
    private int _activeCircuits;

    public int ActiveCircuits => Math.Max(0, Volatile.Read(ref _activeCircuits));

    public void CircuitOpened() => Interlocked.Increment(ref _activeCircuits);

    public void CircuitClosed()
    {
        while (true)
        {
            var current = Volatile.Read(ref _activeCircuits);
            if (current == 0 || Interlocked.CompareExchange(ref _activeCircuits, current - 1, current) == current) return;
        }
    }
}

public sealed class CircuitMetricsHandler(CircuitMetrics metrics) : CircuitHandler
{
    public void CircuitOpened(Circuit circuit) => metrics.CircuitOpened();

    public void CircuitClosed(Circuit circuit) => metrics.CircuitClosed();

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        CircuitOpened(circuit);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        CircuitClosed(circuit);
        return Task.CompletedTask;
    }
}

public static class WebMetricsEndpoint
{
    public static IEndpointRouteBuilder MapKscWebMetrics(this IEndpointRouteBuilder endpoints, IConfiguration configuration)
    {
        var token = configuration["Metrics:Token"];
        if (string.IsNullOrWhiteSpace(token) || token.Length < 16) return endpoints;

        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        endpoints.MapGet("/metrics", (HttpContext context, CircuitMetrics circuits) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? SHA256.HashData(Encoding.UTF8.GetBytes(header[7..].Trim()))
                : SHA256.HashData([]);
            if (!CryptographicOperations.FixedTimeEquals(supplied, expected)) return Results.Unauthorized();

            return Results.Text(
                "# HELP ksc_blazor_active_circuits Active Blazor Server circuits in this process.\n# TYPE ksc_blazor_active_circuits gauge\n"
                + $"ksc_blazor_active_circuits {circuits.ActiveCircuits}\n",
                "text/plain; version=0.0.4; charset=utf-8");
        }).ExcludeFromDescription();
        return endpoints;
    }
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
