namespace KSC.MeetingManagement.Domain.Meetings;

public enum RoomEntryStatus
{
    /// <summary>Asked to enter and waiting for the host.</summary>
    Waiting = 1,

    /// <summary>The host let them in; they may get a room token.</summary>
    Admitted = 2,

    /// <summary>The host refused entry.</summary>
    Denied = 3,

    /// <summary>The host removed them from the room; they cannot come back.</summary>
    Removed = 4,
}

public enum RoomEntryKind
{
    Member = 1,
    Guest = 2,
}

/// <summary>
/// One person's standing at the door of a meeting room: waiting, admitted, denied or removed. Identity is the media identity
/// ("u-…" for members, "g-…" for guests). Denied and removed are final for the meeting; only the host moves people out of Waiting.
/// </summary>
public sealed class RoomEntry
{
    public const int MaxNameLength = 100;

    public RoomEntry(Guid id, Guid tenantId, Guid meetingId, string identity, RoomEntryKind kind, string displayName, DateTimeOffset requestedAtUtc)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || meetingId == Guid.Empty)
        {
            throw new MeetingDomainException("Entry, tenant and meeting ids are required.");
        }

        if (string.IsNullOrWhiteSpace(identity) || identity.Length > 128)
        {
            throw new MeetingDomainException("A valid identity is required.");
        }

        Id = id;
        TenantId = tenantId;
        MeetingId = meetingId;
        Identity = identity;
        Kind = kind;
        DisplayName = Clean(displayName);
        RequestedAtUtc = requestedAtUtc;
        Status = RoomEntryStatus.Waiting;
    }

    public Guid Id { get; }
    public Guid TenantId { get; }
    public Guid MeetingId { get; }
    public string Identity { get; }
    public RoomEntryKind Kind { get; }
    public string DisplayName { get; private set; }
    public RoomEntryStatus Status { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; }
    public DateTimeOffset? DecidedAtUtc { get; private set; }
    public Guid? DecidedByUserId { get; private set; }

    /// <summary>Concurrency token so two hosts cannot decide the same person differently.</summary>
    public long DecisionVersion { get; private set; }

    public void Admit(Guid actorUserId, DateTimeOffset nowUtc)
    {
        Require(Status == RoomEntryStatus.Waiting, "Only a waiting person can be admitted.");
        Decide(RoomEntryStatus.Admitted, actorUserId, nowUtc);
    }

    public void Deny(Guid actorUserId, DateTimeOffset nowUtc)
    {
        Require(Status == RoomEntryStatus.Waiting, "Only a waiting person can be denied.");
        Decide(RoomEntryStatus.Denied, actorUserId, nowUtc);
    }

    /// <summary>Removes a person who is waiting or already inside. Final: they cannot enter again.</summary>
    public void Remove(Guid actorUserId, DateTimeOffset nowUtc)
    {
        Require(Status is RoomEntryStatus.Waiting or RoomEntryStatus.Admitted, "Only someone waiting or admitted can be removed.");
        Decide(RoomEntryStatus.Removed, actorUserId, nowUtc);
    }

    /// <summary>A member who already is allowed in (for example a participant) is recorded as admitted without waiting.</summary>
    public void AdmitAutomatically(DateTimeOffset nowUtc)
    {
        Require(Status == RoomEntryStatus.Waiting, "Only a waiting person can be admitted.");
        Status = RoomEntryStatus.Admitted;
        DecidedAtUtc = nowUtc;
        DecisionVersion++;
    }

    private void Decide(RoomEntryStatus status, Guid actorUserId, DateTimeOffset nowUtc)
    {
        if (actorUserId == Guid.Empty)
        {
            throw new MeetingDomainException("The acting user is required.");
        }

        Status = status;
        DecidedByUserId = actorUserId;
        DecidedAtUtc = nowUtc;
        DecisionVersion++;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new MeetingDomainException(message);
        }
    }

    private static string Clean(string? name)
    {
        var cleaned = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? "participant" : cleaned.Length > MaxNameLength ? cleaned[..MaxNameLength] : cleaned;
    }
}
