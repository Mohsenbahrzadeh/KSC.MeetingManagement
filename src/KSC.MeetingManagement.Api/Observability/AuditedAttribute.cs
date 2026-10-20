using System.Security.Claims;
using KSC.MeetingManagement.Application.Audit;
using KSC.MeetingManagement.Domain.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace KSC.MeetingManagement.Api.Observability;

/// <summary>
/// Records an audit event for the action after it ran: who acted, what, on which resource (taken from a route value),
/// and whether it succeeded, was denied or failed. Only the verb, the resource id and the status code are stored,
/// never request bodies, so no secrets or personal text reach the audit log.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AuditedAttribute(string action, string resourceType, string resourceRouteKey = "id") : Attribute, IAsyncActionFilter
{
    /// <summary>Set for endpoints called by invited guests; the actor is then the guest, identified by the participantId route value.</summary>
    public bool Guest { get; set; }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var executed = await next();
        var status = executed.Exception is not null && !executed.ExceptionHandled
            ? StatusCodes.Status500InternalServerError
            : StatusOf(executed.Result);

        var http = context.HttpContext;
        var audit = http.RequestServices.GetService<IAuditLog>();
        if (audit is null)
        {
            return;
        }

        var (actorType, actorId) = Guest
            ? (AuditActorType.Guest, RouteGuid(http, "participantId"))
            : ActorOf(http.User);

        // Creating something has no id in the route yet; take it from the returned object.
        var resourceId = RouteGuid(http, resourceRouteKey) ?? CreatedId(executed.Result);

        await audit.RecordAsync(
            new AuditEntry(action, resourceType, resourceId, OutcomeOf(status), actorType, actorId, $"status {status}"),
            http.RequestAborted);
    }

    public static AuditOutcome OutcomeOf(int status) => status switch
    {
        >= 200 and < 300 => AuditOutcome.Success,
        StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden => AuditOutcome.Denied,
        _ => AuditOutcome.Failure,
    };

    internal static (AuditActorType Type, Guid? Id) ActorOf(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? (AuditActorType.User, id) : (AuditActorType.Anonymous, null);
    }

    internal static Guid? RouteGuid(HttpContext http, string key) =>
        http.Request.RouteValues.TryGetValue(key, out var value) && Guid.TryParse(value?.ToString(), out var id) ? id : null;

    private static Guid? CreatedId(IActionResult? result) =>
        result is ObjectResult { Value: { } value } && value.GetType().GetProperty("Id")?.GetValue(value) is Guid id && id != Guid.Empty ? id : null;

    private static int StatusOf(IActionResult? result) => result switch
    {
        ObjectResult { StatusCode: { } code } => code,
        ObjectResult => StatusCodes.Status200OK,
        StatusCodeResult s => s.StatusCode,
        FileResult => StatusCodes.Status200OK,
        EmptyResult => StatusCodes.Status200OK,
        null => StatusCodes.Status200OK,
        _ => StatusCodes.Status200OK,
    };
}

/// <summary>Request facts for the application layer (correlation id).</summary>
public sealed class HttpRequestInfo(IHttpContextAccessor accessor) : IRequestInfo
{
    public string? CorrelationId => accessor.HttpContext is { } http ? CorrelationIdMiddleware.Get(http) : null;
}

/// <summary>Records every refused request (HTTP 403) before the standard response is written.</summary>
public sealed class AuditingAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (authorizeResult.Forbidden)
        {
            Application.Telemetry.KscTelemetry.AuthorizationDenied.Add(1);
        }

        if (authorizeResult.Forbidden && context.RequestServices.GetService<IAuditLog>() is { } audit)
        {
            var (actorType, actorId) = AuditedAttribute.ActorOf(context.User);
            var pattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unknown";
            var resource = AuditedAttribute.RouteGuid(context, "meetingId") ?? AuditedAttribute.RouteGuid(context, "id");
            await audit.RecordAsync(
                new AuditEntry("authorization.denied", "endpoint", resource, AuditOutcome.Denied, actorType, actorId, $"{context.Request.Method} {pattern}"),
                context.RequestAborted);
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
