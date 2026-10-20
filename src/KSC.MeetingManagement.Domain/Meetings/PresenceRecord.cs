namespace KSC.MeetingManagement.Domain.Meetings;

/// <summary>
/// One continuous stay of a person in a meeting room, built only from validated media-server events: when they came in and, once known,
/// when they left. A person who reconnects has several records.
/// </summary>
public sealed class PresenceRecord
{
    public PresenceRecord(Guid id, Guid tenantId, Guid meetingId, string identity, string displayName, DateTimeOffset joinedAtUtc)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || meetingId == Guid.Empty)
        {
            throw new MeetingDomainException("Presence, tenant and meeting ids are required.");
        }

        if (string.IsNullOrWhiteSpace(identity) || identity.Length > 128)
        {
            throw new MeetingDomainException("A valid identity is required.");
        }

        Id = id;
        TenantId = tenantId;
        MeetingId = meetingId;
        Identity = identity;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? "participant" : displayName.Trim()[..Math.Min(displayName.Trim().Length, RoomEntry.MaxNameLength)];
        JoinedAtUtc = joinedAtUtc;
    }

    public Guid Id { get; }
    public Guid TenantId { get; }
    public Guid MeetingId { get; }
    public string Identity { get; }
    public string DisplayName { get; }
    public DateTimeOffset JoinedAtUtc { get; }
    public DateTimeOffset? LeftAtUtc { get; private set; }

    public bool IsOpen => LeftAtUtc is null;

    /// <summary>Time spent in the room. For a stay that is still open, measured up to <paramref name="openUntilUtc"/>; never negative.</summary>
    public TimeSpan Duration(DateTimeOffset openUntilUtc)
    {
        var end = LeftAtUtc ?? openUntilUtc;
        return end > JoinedAtUtc ? end - JoinedAtUtc : TimeSpan.Zero;
    }

    /// <summary>Closes the stay. A leave time before the join time is raised to the join time (clock differences), so a stay is never negative.</summary>
    public void Leave(DateTimeOffset leftAtUtc)
    {
        if (LeftAtUtc is not null)
        {
            throw new MeetingDomainException("The stay is already closed.");
        }

        LeftAtUtc = leftAtUtc < JoinedAtUtc ? JoinedAtUtc : leftAtUtc;
    }
}

/// <summary>Remembers an event id that was already processed so a repeated delivery changes nothing.</summary>
public sealed class ProcessedMediaEvent
{
    public ProcessedMediaEvent(Guid id, Guid tenantId, string eventId, string type, DateTimeOffset receivedAtUtc)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || string.IsNullOrWhiteSpace(eventId) || eventId.Length > 128)
        {
            throw new MeetingDomainException("Valid ids are required.");
        }

        Id = id;
        TenantId = tenantId;
        EventId = eventId;
        Type = type.Length > 40 ? type[..40] : type;
        ReceivedAtUtc = receivedAtUtc;
    }

    public Guid Id { get; }
    public Guid TenantId { get; }
    public string EventId { get; }
    public string Type { get; }
    public DateTimeOffset ReceivedAtUtc { get; }
}
