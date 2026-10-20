using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using KSC.MeetingManagement.Application.Media;
using Microsoft.AspNetCore.RateLimiting;

namespace KSC.MeetingManagement.Api.Security;

public static class SecurityExtensions
{
    public const string GuestPolicy = "guest";
    public const string SensitivePolicy = "sensitive";
    public const string CorsPolicyName = "ksc-cors";

    /// <summary>
    /// Request limits. "guest" protects anonymous invitation endpoints (guessing), "sensitive" protects token issuing, downloads and reports,
    /// and a global per-client limit caps everything else. Limits are per client address; behind a reverse proxy the proxy must pass the real address.
    /// </summary>
    public static IServiceCollection AddKscRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var guest = Limit(configuration, "RateLimit:GuestPerMinute", 20);
        var sensitive = Limit(configuration, "RateLimit:SensitivePerMinute", 60);
        var global = Limit(configuration, "RateLimit:GlobalPerMinute", 600);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                Application.Telemetry.KscTelemetry.RateLimited.Add(1);
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { title = "تعداد درخواست‌ها زیاد است", status = 429, detail = "لطفاً کمی بعد دوباره تلاش کنید." },
                    cancellationToken);
            };

            options.AddPolicy(GuestPolicy, http => PerMinute($"guest:{Client(http)}", guest));
            options.AddPolicy(SensitivePolicy, http => PerMinute($"sensitive:{UserOrClient(http)}", sensitive));
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => PerMinute($"global:{Client(http)}", global));
        });
        return services;
    }

    /// <summary>
    /// Allows browser calls only from the origins listed in Cors:AllowedOrigins (comma separated). With no list, no cross-origin access is granted.
    /// </summary>
    public static IServiceCollection AddKscCors(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var origins = (configuration["Cors:AllowedOrigins"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(o => Uri.TryCreate(o, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/" && o != "*")
            .ToArray();

        services.AddCors(options =>
        {
            if (origins.Length > 0)
            {
                options.AddPolicy(CorsPolicyName, policy => policy
                    .WithOrigins(origins)
                    .WithMethods("GET", "POST", "PUT", "DELETE")
                    .WithHeaders("Authorization", "Content-Type", "X-Tenant-Id", "X-Correlation-Id")
                    .WithExposedHeaders("X-Correlation-Id", "Retry-After")
                    .SetPreflightMaxAge(TimeSpan.FromMinutes(10)));
            }
        });
        return services;
    }

    /// <summary>Stops startup when media settings are present but unusable, instead of failing later at the first call.</summary>
    public static void ValidateKscConfiguration(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new MediaOptions
        {
            ServerUrl = configuration["Media:ServerUrl"],
            ApiKey = configuration["Media:ApiKey"],
            ApiSecret = configuration["Media:ApiSecret"],
        };
        var anyMedia = !string.IsNullOrWhiteSpace(options.ServerUrl) || !string.IsNullOrWhiteSpace(options.ApiKey) || !string.IsNullOrWhiteSpace(options.ApiSecret);
        if (anyMedia && !options.IsComplete)
        {
            throw new InvalidOperationException(
                $"Media settings are incomplete: ServerUrl, ApiKey and an ApiSecret of at least {MediaOptions.MinSecretLength} characters are all required.");
        }
    }

    private static RateLimitPartition<string> PerMinute(string key, int limit) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    private static string Client(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string UserOrClient(HttpContext http) =>
        http.User.Identity?.IsAuthenticated == true
            ? "u:" + (http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? Client(http))
            : "ip:" + Client(http);

    private static int Limit(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;
}
