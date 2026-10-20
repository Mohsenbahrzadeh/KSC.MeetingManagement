namespace KSC.MeetingManagement.Domain.Audit;

public enum AuditOutcome
{
    Success = 1,
    Denied = 2,
    Failure = 3,
}

public enum AuditActorType
{
    User = 1,
    Guest = 2,
    Anonymous = 3,
    System = 4,
}

/// <summary>
/// One recorded sensitive event: who did what to which resource, when, and with which result.
/// Append-only: the application never updates or deletes audit events. Details must never contain secrets or personal text.
/// </summary>
public sealed class AuditEvent
{
    public const int MaxActionLength = 100;
    public const int MaxResourceTypeLength = 50;
    public const int MaxDetailLength = 500;

    public AuditEvent(
        Guid id,
        Guid tenantId,
        DateTimeOffset occurredAtUtc,
        AuditActorType actorType,
        Guid? actorId,
        string action,
        string resourceType,
        Guid? resourceId,
        AuditOutcome outcome,
        string? correlationId,
        string? detail)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("Audit event and tenant ids are required.");
        }

        if (string.IsNullOrWhiteSpace(action) || action.Length > MaxActionLength)
        {
            throw new ArgumentException($"Action is required and at most {MaxActionLength} characters.", nameof(action));
        }

        if (string.IsNullOrWhiteSpace(resourceType) || resourceType.Length > MaxResourceTypeLength)
        {
            throw new ArgumentException($"Resource type is required and at most {MaxResourceTypeLength} characters.", nameof(resourceType));
        }

        Id = id;
        TenantId = tenantId;
        OccurredAtUtc = occurredAtUtc;
        ActorType = actorType;
        ActorId = actorId;
        Action = action;
        ResourceType = resourceType;
        ResourceId = resourceId;
        Outcome = outcome;
        CorrelationId = correlationId is { Length: > 64 } ? correlationId[..64] : correlationId;
        Detail = detail is { Length: > MaxDetailLength } ? detail[..MaxDetailLength] : detail;
    }

    public Guid Id { get; }
    public Guid TenantId { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public AuditActorType ActorType { get; }
    public Guid? ActorId { get; }

    /// <summary>Dotted verb such as "meeting.cancel" or "minutes.approve".</summary>
    public string Action { get; }

    public string ResourceType { get; }
    public Guid? ResourceId { get; }
    public AuditOutcome Outcome { get; }

    /// <summary>Ties the event to the request and its log lines.</summary>
    public string? CorrelationId { get; }

    public string? Detail { get; }
}
