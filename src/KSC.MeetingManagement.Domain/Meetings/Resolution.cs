namespace KSC.MeetingManagement.Domain.Meetings;

public enum ResolutionStatus
{
    Draft = 1,
    Adopted = 2,
    Rejected = 3,
    Revoked = 4,
}

/// <summary>
/// A decision taken in a meeting, optionally tied to an agenda topic of the same meeting. Draft text can change;
/// once adopted the text is fixed and the only change left is a recorded revocation.
/// </summary>
public sealed class Resolution
{
    public const int MaxTextLength = 4000;
    public const int MaxReasonLength = 2000;

    public Resolution(Guid id, Guid tenantId, Guid meetingId, int number, string text, Guid? agendaItemId)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || meetingId == Guid.Empty)
        {
            throw new MeetingDomainException("Resolution, tenant and meeting ids are required.");
        }

        if (number < 1)
        {
            throw new MeetingDomainException("Resolution number starts at 1.");
        }

        if (agendaItemId == Guid.Empty)
        {
            throw new MeetingDomainException("Agenda item id is invalid.");
        }

        Id = id;
        TenantId = tenantId;
        MeetingId = meetingId;
        Number = number;
        Text = RequireText(text);
        AgendaItemId = agendaItemId;
        Status = ResolutionStatus.Draft;
    }

    public Guid Id { get; }
    public Guid TenantId { get; }
    public Guid MeetingId { get; }

    /// <summary>1..n within the meeting; assigned once and never reused.</summary>
    public int Number { get; private set; }

    public string Text { get; private set; }
    public Guid? AgendaItemId { get; private set; }
    public ResolutionStatus Status { get; private set; }
    public DateTimeOffset? DecidedAtUtc { get; private set; }
    public Guid? DecidedByUserId { get; private set; }
    public string? RevocationReason { get; private set; }
    public long LockVersion { get; private set; }

    /// <summary>Sets the number before the resolution is first saved; used when a concurrent request took the same number.</summary>
    public void AssignNumber(int number)
    {
        if (number < 1)
        {
            throw new MeetingDomainException("Resolution number starts at 1.");
        }

        Number = number;
    }

    public void Edit(string text, Guid? agendaItemId)
    {
        Require(Status == ResolutionStatus.Draft, "Only a draft resolution can be edited.");
        if (agendaItemId == Guid.Empty)
        {
            throw new MeetingDomainException("Agenda item id is invalid.");
        }

        Text = RequireText(text);
        AgendaItemId = agendaItemId;
        LockVersion++;
    }

    public void Adopt(Guid actorUserId, DateTimeOffset nowUtc)
    {
        Require(Status == ResolutionStatus.Draft, "Only a draft resolution can be adopted.");
        Decide(ResolutionStatus.Adopted, actorUserId, nowUtc);
    }

    public void Reject(Guid actorUserId, DateTimeOffset nowUtc)
    {
        Require(Status == ResolutionStatus.Draft, "Only a draft resolution can be rejected.");
        Decide(ResolutionStatus.Rejected, actorUserId, nowUtc);
    }

    public void Revoke(Guid actorUserId, string reason, DateTimeOffset nowUtc)
    {
        Require(Status == ResolutionStatus.Adopted, "Only an adopted resolution can be revoked.");
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new MeetingDomainException("A reason is required to revoke a resolution.");
        }

        if (reason.Trim().Length > MaxReasonLength)
        {
            throw new MeetingDomainException($"The reason cannot exceed {MaxReasonLength} characters.");
        }

        Decide(ResolutionStatus.Revoked, actorUserId, nowUtc);
        RevocationReason = reason.Trim();
    }

    private void Decide(ResolutionStatus status, Guid actorUserId, DateTimeOffset nowUtc)
    {
        if (actorUserId == Guid.Empty)
        {
            throw new MeetingDomainException("The acting user is required.");
        }

        Status = status;
        DecidedByUserId = actorUserId;
        DecidedAtUtc = nowUtc;
        LockVersion++;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new MeetingDomainException(message);
        }
    }

    private static string RequireText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new MeetingDomainException("Resolution text is required.");
        }

        return text.Trim().Length > MaxTextLength
            ? throw new MeetingDomainException($"Resolution text cannot exceed {MaxTextLength} characters.")
            : text.Trim();
    }
}
