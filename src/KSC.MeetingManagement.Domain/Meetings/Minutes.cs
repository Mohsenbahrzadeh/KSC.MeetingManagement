namespace KSC.MeetingManagement.Domain.Meetings;

public enum MinutesStatus
{
    Draft = 1,
    InReview = 2,
    Approved = 3,
    Rejected = 4,
}

/// <summary>One immutable saved text of the minutes.</summary>
public sealed class MinutesVersion
{
    public MinutesVersion(Guid minutesId, Guid tenantId, int number, string content, Guid authorUserId, DateTimeOffset createdAtUtc)
    {
        MinutesId = minutesId;
        TenantId = tenantId;
        Number = number;
        Content = content;
        AuthorUserId = authorUserId;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid MinutesId { get; }
    public Guid TenantId { get; }
    public int Number { get; }
    public string Content { get; }
    public Guid AuthorUserId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
}

/// <summary>A recorded change of status: who did it, when, on which version and why.</summary>
public sealed class MinutesTransition
{
    public MinutesTransition(
        Guid id, Guid minutesId, Guid tenantId, MinutesStatus from, MinutesStatus to, int versionNumber, Guid actorUserId, DateTimeOffset atUtc, string? comment)
    {
        Id = id;
        MinutesId = minutesId;
        TenantId = tenantId;
        From = from;
        To = to;
        VersionNumber = versionNumber;
        ActorUserId = actorUserId;
        AtUtc = atUtc;
        Comment = comment;
    }

    public Guid Id { get; }
    public Guid MinutesId { get; }
    public Guid TenantId { get; }
    public MinutesStatus From { get; }
    public MinutesStatus To { get; }
    public int VersionNumber { get; }
    public Guid ActorUserId { get; }
    public DateTimeOffset AtUtc { get; }
    public string? Comment { get; }
}

/// <summary>
/// The minutes of one meeting. Every save creates a new immutable version. Status moves only through
/// Draft → InReview → Approved or Rejected; an approved text can only be changed by an explicit reopen, which is
/// recorded and sends the minutes through review again.
/// </summary>
public sealed class Minutes
{
    public const int MaxContentLength = 200_000;
    public const int MaxCommentLength = 2000;

    private readonly List<MinutesVersion> _versions = [];
    private readonly List<MinutesTransition> _transitions = [];

    // Used by EF Core when materialising stored minutes.
    private Minutes()
    {
    }

    public Minutes(Guid id, Guid tenantId, Guid meetingId, string content, Guid authorUserId, DateTimeOffset nowUtc)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || meetingId == Guid.Empty)
        {
            throw new MeetingDomainException("Minutes, tenant and meeting ids are required.");
        }

        Id = id;
        TenantId = tenantId;
        MeetingId = meetingId;
        Status = MinutesStatus.Draft;
        AddVersion(content, authorUserId, nowUtc);
    }

    public Guid Id { get; }
    public Guid TenantId { get; }
    public Guid MeetingId { get; }
    public MinutesStatus Status { get; private set; }
    public int CurrentVersionNumber { get; private set; }

    /// <summary>Version that was approved, or null if none is approved at the moment.</summary>
    public int? ApprovedVersionNumber { get; private set; }

    /// <summary>Concurrency token incremented on every change.</summary>
    public long LockVersion { get; private set; }

    public IReadOnlyList<MinutesVersion> Versions => _versions;
    public IReadOnlyList<MinutesTransition> Transitions => _transitions;

    public MinutesVersion CurrentVersion => _versions.Single(v => v.Number == CurrentVersionNumber);

    /// <summary>Saves new text as a new version. Allowed only while the minutes are a draft or were rejected. Identical text changes nothing.</summary>
    public bool SaveDraft(string content, Guid authorUserId, DateTimeOffset nowUtc)
    {
        if (Status is MinutesStatus.InReview or MinutesStatus.Approved)
        {
            throw new MeetingDomainException(Status == MinutesStatus.Approved
                ? "Approved minutes cannot be edited; reopen them first."
                : "Minutes under review cannot be edited.");
        }

        var normalized = RequireContent(content);
        if (string.Equals(normalized, CurrentVersion.Content, StringComparison.Ordinal))
        {
            return false;
        }

        if (Status == MinutesStatus.Rejected)
        {
            Record(MinutesStatus.Rejected, MinutesStatus.Draft, authorUserId, nowUtc, "Editing after rejection", CurrentVersionNumber + 1);
            Status = MinutesStatus.Draft;
        }

        AddVersion(normalized, authorUserId, nowUtc);
        return true;
    }

    public void Submit(Guid actorUserId, DateTimeOffset nowUtc)
    {
        Require(Status == MinutesStatus.Draft, "Only a draft can be submitted for approval.");
        Move(MinutesStatus.InReview, actorUserId, nowUtc, null);
    }

    public void Approve(Guid actorUserId, string? comment, DateTimeOffset nowUtc)
    {
        Require(Status == MinutesStatus.InReview, "Only minutes under review can be approved.");
        Move(MinutesStatus.Approved, actorUserId, nowUtc, NormalizeComment(comment, required: false));
        ApprovedVersionNumber = CurrentVersionNumber;
    }

    public void Reject(Guid actorUserId, string comment, DateTimeOffset nowUtc)
    {
        Require(Status == MinutesStatus.InReview, "Only minutes under review can be rejected.");
        Move(MinutesStatus.Rejected, actorUserId, nowUtc, NormalizeComment(comment, required: true));
    }

    /// <summary>The only way to change approved minutes: records who reopened them and why, and returns them to draft.</summary>
    public void Reopen(Guid actorUserId, string reason, DateTimeOffset nowUtc)
    {
        Require(Status == MinutesStatus.Approved, "Only approved minutes can be reopened.");
        Move(MinutesStatus.Draft, actorUserId, nowUtc, NormalizeComment(reason, required: true));
        ApprovedVersionNumber = null;
    }

    private void Move(MinutesStatus to, Guid actorUserId, DateTimeOffset nowUtc, string? comment)
    {
        Record(Status, to, actorUserId, nowUtc, comment, CurrentVersionNumber);
        Status = to;
        LockVersion++;
    }

    private void Record(MinutesStatus from, MinutesStatus to, Guid actorUserId, DateTimeOffset nowUtc, string? comment, int versionNumber)
    {
        if (actorUserId == Guid.Empty)
        {
            throw new MeetingDomainException("The acting user is required.");
        }

        _transitions.Add(new MinutesTransition(Guid.NewGuid(), Id, TenantId, from, to, versionNumber, actorUserId, nowUtc, comment));
    }

    private void AddVersion(string content, Guid authorUserId, DateTimeOffset nowUtc)
    {
        if (authorUserId == Guid.Empty)
        {
            throw new MeetingDomainException("The author is required.");
        }

        CurrentVersionNumber++;
        _versions.Add(new MinutesVersion(Id, TenantId, CurrentVersionNumber, RequireContent(content), authorUserId, nowUtc));
        LockVersion++;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new MeetingDomainException(message);
        }
    }

    private static string RequireContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new MeetingDomainException("Minutes text is required.");
        }

        if (content.Length > MaxContentLength)
        {
            throw new MeetingDomainException($"Minutes text cannot exceed {MaxContentLength} characters.");
        }

        return content.Trim();
    }

    private static string? NormalizeComment(string? comment, bool required)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return required ? throw new MeetingDomainException("A comment is required.") : null;
        }

        return comment.Trim().Length > MaxCommentLength
            ? throw new MeetingDomainException($"A comment cannot exceed {MaxCommentLength} characters.")
            : comment.Trim();
    }
}
