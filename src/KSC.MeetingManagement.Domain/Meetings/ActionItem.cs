namespace KSC.MeetingManagement.Domain.Meetings;

public enum ActionItemStatus
{
    Open = 1,
    InProgress = 2,
    Done = 3,
    Cancelled = 4,
}

/// <summary>A follow-up task agreed in a meeting: one owner, a due day and a recorded outcome.</summary>
public sealed class ActionItem
{
    public const int MaxTitleLength = 300;
    public const int MaxDescriptionLength = 4000;
    public const int MaxNoteLength = 2000;

    public ActionItem(
        Guid id, Guid tenantId, Guid meetingId, Guid? resolutionId, string title, string? description, Guid ownerUserId, DateOnly dueDate)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || meetingId == Guid.Empty)
        {
            throw new MeetingDomainException("Action, tenant and meeting ids are required.");
        }

        if (resolutionId == Guid.Empty)
        {
            throw new MeetingDomainException("Resolution id is invalid.");
        }

        Id = id;
        TenantId = tenantId;
        MeetingId = meetingId;
        ResolutionId = resolutionId;
        Status = ActionItemStatus.Open;
        Title = string.Empty;
        OwnerUserId = ownerUserId;
        Apply(title, description, ownerUserId, dueDate);
    }

    public Guid Id { get; }
    public Guid TenantId { get; }
    public Guid MeetingId { get; }
    public Guid? ResolutionId { get; }
    public string Title { get; private set; }
    public string? Description { get; private set; }
    public Guid OwnerUserId { get; private set; }

    /// <summary>The due day in the organisation's calendar day (no time of day).</summary>
    public DateOnly DueDate { get; private set; }

    public ActionItemStatus Status { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public Guid? CompletedByUserId { get; private set; }
    public string? Note { get; private set; }
    public long LockVersion { get; private set; }

    public bool IsActive => Status is ActionItemStatus.Open or ActionItemStatus.InProgress;

    /// <summary>Active and past its due day. A task due today is not yet overdue.</summary>
    public bool IsOverdue(DateOnly today) => IsActive && DueDate < today;

    public void Update(string title, string? description, Guid ownerUserId, DateOnly dueDate)
    {
        RequireActive("Only an open action can be edited.");
        Apply(title, description, ownerUserId, dueDate);
        LockVersion++;
    }

    public void Start()
    {
        Require(Status == ActionItemStatus.Open, "Only an open action can be started.");
        Status = ActionItemStatus.InProgress;
        LockVersion++;
    }

    public void Complete(Guid actorUserId, string? note, DateTimeOffset nowUtc)
    {
        RequireActive("Only an open action can be completed.");
        if (actorUserId == Guid.Empty)
        {
            throw new MeetingDomainException("The acting user is required.");
        }

        var normalized = NormalizeNote(note, required: false);
        Status = ActionItemStatus.Done;
        CompletedAtUtc = nowUtc;
        CompletedByUserId = actorUserId;
        Note = normalized;
        LockVersion++;
    }

    public void Cancel(string reason)
    {
        RequireActive("Only an open action can be cancelled.");
        var normalized = NormalizeNote(reason, required: true);
        Status = ActionItemStatus.Cancelled;
        Note = normalized;
        LockVersion++;
    }

    private void Apply(string title, string? description, Guid ownerUserId, DateOnly dueDate)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new MeetingDomainException("Action title is required.");
        }

        if (title.Trim().Length > MaxTitleLength)
        {
            throw new MeetingDomainException($"Action title cannot exceed {MaxTitleLength} characters.");
        }

        if (description is { Length: > MaxDescriptionLength })
        {
            throw new MeetingDomainException($"Action description cannot exceed {MaxDescriptionLength} characters.");
        }

        if (ownerUserId == Guid.Empty)
        {
            throw new MeetingDomainException("An owner is required.");
        }

        Title = title.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        OwnerUserId = ownerUserId;
        DueDate = dueDate;
    }

    private void RequireActive(string message) => Require(IsActive, message);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new MeetingDomainException(message);
        }
    }

    private static string? NormalizeNote(string? note, bool required)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return required ? throw new MeetingDomainException("A note is required.") : null;
        }

        return note.Trim().Length > MaxNoteLength
            ? throw new MeetingDomainException($"A note cannot exceed {MaxNoteLength} characters.")
            : note.Trim();
    }
}
