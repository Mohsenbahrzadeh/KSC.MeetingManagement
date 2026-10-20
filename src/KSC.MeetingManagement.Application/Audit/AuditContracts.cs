using KSC.MeetingManagement.Domain.Audit;

namespace KSC.MeetingManagement.Application.Audit;

/// <summary>What the caller knows about an event. Tenant, time and correlation id are added by the log.</summary>
public sealed record AuditEntry(
    string Action,
    string ResourceType,
    Guid? ResourceId,
    AuditOutcome Outcome,
    AuditActorType ActorType,
    Guid? ActorId,
    string? Detail = null);

/// <summary>Append-only sink for audit events. Recording must never throw into the request; failures are logged by the implementation.</summary>
public interface IAuditLog
{
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken);
}

public sealed record AuditEventDto(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    AuditActorType ActorType,
    Guid? ActorId,
    string Action,
    string ResourceType,
    Guid? ResourceId,
    AuditOutcome Outcome,
    string? CorrelationId,
    string? Detail)
{
    public static AuditEventDto From(AuditEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new(e.Id, e.OccurredAtUtc, e.ActorType, e.ActorId, e.Action, e.ResourceType, e.ResourceId, e.Outcome, e.CorrelationId, e.Detail);
    }
}

public sealed record AuditQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Action,
    Guid? ActorId,
    Guid? ResourceId,
    AuditOutcome? Outcome,
    int Skip,
    int Take);

/// <summary>Read side of the audit log. Implementations must only return events of the current tenant.</summary>
public interface IAuditQuery
{
    Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken cancellationToken);
}

/// <summary>Request facts the audit log needs but the application layer cannot see.</summary>
public interface IRequestInfo
{
    string? CorrelationId { get; }
}

public sealed class AuditQueryService(IAuditQuery query)
{
    public const int MaxPageSize = 200;
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);

    public async Task<IReadOnlyList<AuditEventDto>> QueryAsync(AuditQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.From is { } from && request.To is { } to)
        {
            if (to <= from)
            {
                throw new ArgumentException("Range end must be after its start.", nameof(request));
            }

            if (to - from > MaxRange)
            {
                throw new ArgumentException($"The range cannot be longer than {MaxRange.TotalDays:0} days.", nameof(request));
            }
        }

        var bounded = request with { Skip = Math.Max(0, request.Skip), Take = Math.Clamp(request.Take, 1, MaxPageSize) };
        return (await query.QueryAsync(bounded, cancellationToken)).Select(AuditEventDto.From).ToList();
    }
}
