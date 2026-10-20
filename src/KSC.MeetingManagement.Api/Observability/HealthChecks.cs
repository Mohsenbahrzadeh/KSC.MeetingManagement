using System.Text.Json;
using KSC.MeetingManagement.Application.Media;
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
        services.AddSingleton<MetricsCollector>();
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
            .AddCheck<StorageHealthCheck>("storage", tags: ["ready"])
            .AddCheck<MediaConfigurationHealthCheck>("media", tags: ["ready"]);
        return services;
    }

    /// <summary>
    /// /health/live answers whether the process runs (no dependencies). /health/ready also checks database, storage and media settings:
    /// 503 when a required part is down, 200 with status "Degraded" when only the optional media part is missing.
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
