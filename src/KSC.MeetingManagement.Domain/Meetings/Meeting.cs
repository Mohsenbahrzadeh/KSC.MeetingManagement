namespace KSC.MeetingManagement.Domain.Meetings;

public enum MeetingStatus
{
    Draft = 1,
    Scheduled = 2,
    InProgress = 3,
    Completed = 4,
    Cancelled = 5,
    Postponed = 6,
}

public enum MeetingMode
{
    InPerson = 1,
    Online = 2,
    Hybrid = 3,
}

public sealed class MeetingDomainException(string message) : InvalidOperationException(message);

/// <summary>
/// Meeting aggregate. All status changes go through methods that enforce the allowed transitions;
/// status is never set directly. Times are stored as UTC instants.
/// </summary>
public sealed class Meeting
{
    private static readonly Dictionary<MeetingStatus, MeetingStatus[]> Transitions = new()
    {
        [MeetingStatus.Draft] = [MeetingStatus.Scheduled, MeetingStatus.Cancelled],
        [MeetingStatus.Scheduled] = [MeetingStatus.InProgress, MeetingStatus.Cancelled, MeetingStatus.Postponed],
        [MeetingStatus.Postponed] = [MeetingStatus.Scheduled, MeetingStatus.Cancelled],
        [MeetingStatus.InProgress] = [MeetingStatus.Completed],
        [MeetingStatus.Completed] = [],
        [MeetingStatus.Cancelled] = [],
    };

    public Meeting(Guid id, Guid tenantId, string title, MeetingMode mode, Guid organizerId)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || organizerId == Guid.Empty)
        {
            throw new MeetingDomainException("Meeting, tenant and organizer ids are required.");
        }

        Id = id;
        TenantId = tenantId;
        OrganizerId = organizerId;
        Mode = mode;
        Title = RequireTitle(title);
        Status = MeetingStatus.Draft;
    }

    public Guid Id { get; }
    public Guid TenantId { get; }
    public Guid OrganizerId { get; }
    public string Title { get; private set; }
    public string? Description { get; private set; }
    public MeetingMode Mode { get; private set; }
    public MeetingStatus Status { get; private set; }
    public DateTimeOffset? StartUtc { get; private set; }
    public DateTimeOffset? EndUtc { get; private set; }
    public string? CancellationReason { get; private set; }

    /// <summary>Human-readable number unique within the tenant (for example 2026-0007); assigned once at creation.</summary>
    public string? Number { get; private set; }

    /// <summary>Incremented on every change; used for optimistic concurrency.</summary>
    public long Version { get; private set; }

    public static bool CanTransition(MeetingStatus from, MeetingStatus to) =>
        Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public void AssignNumber(string number)
    {
        if (Number is not null)
        {
            throw new MeetingDomainException("The meeting already has a number.");
        }

        if (string.IsNullOrWhiteSpace(number))
        {
            throw new MeetingDomainException("Meeting number is required.");
        }

        Number = number;
    }

    /// <summary>Used only to retry number assignment after a lost race, before the meeting is persisted.</summary>
    public void ClearNumber() => Number = null;

    public void UpdateDetails(string title, string? description, MeetingMode mode)
    {
        EnsureEditable();
        Title = RequireTitle(title);
        Description = description?.Trim();
        Mode = mode;
        Version++;
    }

    /// <summary>Sets the time and moves Draft or Postponed to Scheduled; also reschedules an already Scheduled meeting.</summary>
    public void Schedule(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            throw new MeetingDomainException("Meeting end must be after its start.");
        }

        if (Status != MeetingStatus.Scheduled)
        {
            MoveTo(MeetingStatus.Scheduled);
        }

        StartUtc = start.ToUniversalTime();
        EndUtc = end.ToUniversalTime();
        Version++;
    }

    public void Postpone()
    {
        MoveTo(MeetingStatus.Postponed);
        StartUtc = null;
        EndUtc = null;
        Version++;
    }

    public void Start()
    {
        MoveTo(MeetingStatus.InProgress);
        Version++;
    }

    public void Complete()
    {
        MoveTo(MeetingStatus.Completed);
        Version++;
    }

    public void Cancel(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new MeetingDomainException("A cancellation reason is required.");
        }

        MoveTo(MeetingStatus.Cancelled);
        CancellationReason = reason.Trim();
        Version++;
    }

    private void MoveTo(MeetingStatus target)
    {
        if (!CanTransition(Status, target))
        {
            throw new MeetingDomainException($"Cannot change meeting status from {Status} to {target}.");
        }

        Status = target;
    }

    private void EnsureEditable()
    {
        if (Status is MeetingStatus.Completed or MeetingStatus.Cancelled or MeetingStatus.InProgress)
        {
            throw new MeetingDomainException($"A meeting in status {Status} cannot be edited.");
        }
    }

    private static string RequireTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new MeetingDomainException("Meeting title is required.");
        }

        return title.Trim();
    }
}
