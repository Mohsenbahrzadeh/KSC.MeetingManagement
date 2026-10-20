using System.Diagnostics;

namespace KSC.MeetingManagement.Api.Observability;

/// <summary>
/// Ensures every request has a correlation id: reuses a valid incoming X-Correlation-Id,
/// otherwise generates one, echoes it in the response and puts it in the log scope.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Resolve(context.Request.Headers[HeaderName].ToString());
        context.Items[HeaderName] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });
        Activity.Current?.SetTag("correlation_id", correlationId);

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    public static string? Get(HttpContext context) =>
        context.Items.TryGetValue(HeaderName, out var v) ? v as string : null;

    // Only accept short, safe values so the header cannot be used for log injection.
    private static string Resolve(string incoming) =>
        incoming.Length is > 0 and <= MaxLength && incoming.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? incoming
            : Guid.NewGuid().ToString("N");
}
