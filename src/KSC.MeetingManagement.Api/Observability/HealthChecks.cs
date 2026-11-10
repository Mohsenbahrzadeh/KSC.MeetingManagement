using System.Text.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using System.Net.Security;
using KSC.MeetingManagement.Application.Media;
using KSC.MeetingManagement.Infrastructure.Media;
using KSC.MeetingManagement.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KSC.MeetingManagement.Api.Observability;

/// <summary>Source of the "can we reach the database" fact; replaceable so a failure can be simulated in tests.</summary>
public interface IDatabaseProbe
{
    Task<bool> CanConnectAsync(CancellationToken cancellationToken);
}

public sealed class EfDatabaseProbe(KscDbContext db) : IDatabaseProbe
{
    public Task<bool> CanConnectAsync(CancellationToken cancellationToken) => db.Database.CanConnectAsync(cancellationToken);
}

public sealed class DatabaseHealthCheck(IDatabaseProbe probe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await probe.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("database unreachable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Details stay in logs; the response only says the check failed.
            return HealthCheckResult.Unhealthy("database check failed");
        }
    }
}

/// <summary>The media server is optional for the rest of the system: missing settings degrade, not fail, readiness.</summary>
public sealed class MediaConfigurationHealthCheck(MediaOptions options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(options.IsComplete ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded("media not configured"));
}

/// <summary>Small HTTP probe for LiveKit's unauthenticated server health endpoint.</summary>
public interface IMediaServerProbe
{
    Task<bool> CanConnectAsync(MediaOptions options, CancellationToken cancellationToken);
}

public sealed class HttpMediaServerProbe(IHttpClientFactory clients) : IMediaServerProbe
{
    public async Task<bool> CanConnectAsync(MediaOptions options, CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient("media-health")
            .GetAsync(new Uri(LiveKitRoomControl.ApiBase(options), "/"), cancellationToken);
        return response.IsSuccessStatusCode;
    }
}

/// <summary>A configured but unreachable media server degrades readiness; failures disclose no endpoint or exception details.</summary>
public sealed class MediaServerHealthCheck(MediaOptions options, IMediaServerProbe probe, ILogger<MediaServerHealthCheck> logger) : IHealthCheck
{
    private static readonly Action<ILogger, Exception?> TimeoutLog = LoggerMessage.Define(LogLevel.Warning, new EventId(6601, "MediaHealthTimeout"), "Media server health check timed out");
    private static readonly Action<ILogger, string, Exception?> FailureLog = LoggerMessage.Define<string>(LogLevel.Warning, new EventId(6602, "MediaHealthFailure"), "Media server health check failed ({FailureType})");

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.IsComplete) return HealthCheckResult.Healthy();

        try
        {
            return await probe.CanConnectAsync(options, cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded("media server unreachable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TimeoutLog(logger, null);
            return HealthCheckResult.Degraded("media server check timed out");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailureLog(logger, ex.GetType().Name, null);
            return HealthCheckResult.Degraded("media server check failed");
        }
    }
}

public interface ITurnServerProbe
{
    Task<bool> CanConnectAsync(TurnOptions options, CancellationToken cancellationToken);
}

/// <summary>Checks TURN listener reachability using a STUN Binding request for UDP, or a TCP connect for TCP/TLS URLs.</summary>
public sealed class SocketTurnServerProbe : ITurnServerProbe
{
    private static readonly byte[] StunMagicCookie = [0x21, 0x12, 0xA4, 0x42];

    public async Task<bool> CanConnectAsync(TurnOptions options, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));

        foreach (var value in options.Urls)
        {
            if (!TryParse(value, out var host, out var port, out var useUdp, out var useTls)) continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));

            try
            {
                if (useUdp)
                {
                    using var socket = new Socket(SocketType.Dgram, ProtocolType.Udp);
                    await socket.ConnectAsync(host, port, timeout.Token);
                    var transactionId = RandomNumberGenerator.GetBytes(12);
                    var request = new byte[20];
                    request[1] = 1; // STUN Binding Request, type 0x0001
                    StunMagicCookie.CopyTo(request.AsSpan(4, 4));
                    transactionId.CopyTo(request.AsSpan(8, 12));
                    await socket.SendAsync(request, SocketFlags.None, timeout.Token);
                    var response = new byte[512];
                    var length = await socket.ReceiveAsync(response, SocketFlags.None, timeout.Token);
                    if (length >= 20 && response[0] == 0x01 && response[1] is 0x01 or 0x11
                        && response.AsSpan(4, 4).SequenceEqual(StunMagicCookie)
                        && response.AsSpan(8, 12).SequenceEqual(transactionId)) return true;
                }
                else
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(host, port, timeout.Token);
                    if (useTls)
                    {
                        using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token);
                    }
                    return true;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Try another configured relay URL until the overall probe deadline expires.
            }
            catch (SocketException)
            {
                // Try another configured relay URL.
            }
            catch (IOException)
            {
                // TLS negotiation or connection setup failed; try another configured relay URL.
            }
        }

        return false;
    }

    private static bool TryParse(string value, out string host, out int port, out bool useUdp, out bool useTls)
    {
        host = string.Empty;
        port = 0;
        useUdp = false;
        var isTls = value.StartsWith("turns:", StringComparison.OrdinalIgnoreCase);
        useTls = isTls;
        var isTurn = isTls || value.StartsWith("turn:", StringComparison.OrdinalIgnoreCase);
        if (!isTurn) return false;

        var separator = value.IndexOf(':');
        var address = value[(separator + 1)..];
        var transportTcp = isTls || address.Contains("transport=tcp", StringComparison.OrdinalIgnoreCase);
        var scheme = transportTcp ? "tcp" : "udp";
        if (!Uri.TryCreate($"{scheme}://{address}", UriKind.Absolute, out var uri)) return false;

        host = uri.Host;
        port = uri.IsDefaultPort || uri.Port <= 0 ? (isTls ? 5349 : 3478) : uri.Port;
        useUdp = !transportTcp;
        return !string.IsNullOrWhiteSpace(host) && port is > 0 and <= 65535;
    }
}

/// <summary>TURN is optional for direct paths, but absent or unreachable relay settings are visible as degraded.</summary>
public sealed class TurnServerHealthCheck(TurnOptions options, ITurnServerProbe probe, ILogger<TurnServerHealthCheck> logger) : IHealthCheck
{
    private static readonly Action<ILogger, Exception?> TimeoutLog = LoggerMessage.Define(LogLevel.Warning, new EventId(6603, "TurnHealthTimeout"), "TURN health check timed out");
    private static readonly Action<ILogger, string, Exception?> FailureLog = LoggerMessage.Define<string>(LogLevel.Warning, new EventId(6604, "TurnHealthFailure"), "TURN health check failed ({FailureType})");

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.IsComplete) return HealthCheckResult.Degraded("turn not configured");

        try
        {
            return await probe.CanConnectAsync(options, cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded("turn server unreachable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TimeoutLog(logger, null);
            return HealthCheckResult.Degraded("turn health check timed out");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailureLog(logger, ex.GetType().Name, null);
            return HealthCheckResult.Degraded("turn health check failed");
        }
    }
}

/// <summary>Checks that file storage can be written to (attachments).</summary>
public sealed class StorageHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var root = configuration["Storage:Root"] ?? Path.Combine(AppContext.BaseDirectory, "App_Data", "files");
        try
        {
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, $".health-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return Task.FromResult(HealthCheckResult.Healthy());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("storage not writable"));
        }
    }
}

public static class HealthEndpoints
{
    public static IServiceCollection AddKscHealth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IDatabaseProbe, EfDatabaseProbe>();
        services.AddHttpClient("media-health", client => client.Timeout = TimeSpan.FromSeconds(3));
        services.AddScoped<IMediaServerProbe, HttpMediaServerProbe>();
        services.AddScoped<ITurnServerProbe, SocketTurnServerProbe>();
        services.AddSingleton<HealthStatusMetrics>();
        services.AddSingleton<MetricsCollector>();
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
            .AddCheck<StorageHealthCheck>("storage", tags: ["ready"])
            .AddCheck<MediaConfigurationHealthCheck>("media", tags: ["ready"])
            .AddCheck<MediaServerHealthCheck>("media-server", tags: ["ready"])
            .AddCheck<TurnServerHealthCheck>("turn", tags: ["ready"]);
        return services;
    }

    /// <summary>
    /// /health/live answers whether the process runs (no dependencies). /health/ready also checks database, storage and media/relay configuration:
    /// 503 when a required part is down, 200 with status "Degraded" when optional media or TURN is unavailable.
    /// </summary>
    public static IEndpointRouteBuilder MapKscHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealthChecks("/health/live", new()
        {
            Predicate = _ => false,
            ResponseWriter = Write,
        });
        endpoints.MapHealthChecks("/health/ready", new()
        {
            Predicate = r => r.Tags.Contains("ready"),
            ResponseWriter = Write,
        });
        return endpoints;
    }

    private static Task Write(HttpContext context, HealthReport report)
    {
        context.RequestServices.GetRequiredService<HealthStatusMetrics>().Update(report);
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new { status = e.Value.Status.ToString(), detail = e.Value.Description, durationMs = (long)e.Value.Duration.TotalMilliseconds }),
        }));
    }
}

/// <summary>Last observed readiness component states, exported as a bounded-cardinality Prometheus gauge.</summary>
public sealed class HealthStatusMetrics
{
    public const string MeterName = "KSC.MeetingManagement.Health";

    private static readonly Meter Meter = new(MeterName, "1.0");
    private readonly ConcurrentDictionary<string, int> _states = new(StringComparer.Ordinal);

    public HealthStatusMetrics() => Meter.CreateObservableGauge("ksc.dependency.health", Observe, description: "Last observed health state: 1 healthy, 0.5 degraded, 0 unhealthy.");

    public void Update(HealthReport report)
    {
        foreach (var (name, entry) in report.Entries)
        {
            var value = entry.Status switch
            {
                HealthStatus.Healthy => 1,
                HealthStatus.Degraded => 0.5,
                _ => 0,
            };
            _states[name] = (int)(value * 2);
        }
    }

    private IEnumerable<Measurement<double>> Observe() => _states.Select(pair =>
        new Measurement<double>(pair.Value / 2d, new KeyValuePair<string, object?>[] { new("component", pair.Key) }));
}
