namespace KSC.MeetingManagement.Domain.Meetings;

public enum ChatMessageStatus
{
    Visible = 1,
    Deleted = 2,
}

/// <summary>A text message in a meeting's room chat. Deleting only hides it: the text is dropped from every response but the record stays.</summary>
public sealed class ChatMessage
{
    public const int MaxTextLength = 2000;

    public ChatMessage(Guid id, Guid tenantId, Guid meetingId, long sequence, Guid senderUserId, string text, DateTimeOffset sentAtUtc)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || meetingId == Guid.Empty || senderUserId == Guid.Empty)
        {
            throw new MeetingDomainException("Message, tenant, meeting and sender ids are required.");
        }

        if (sequence < 1)
        {
            throw new MeetingDomainException("Message sequence starts at 1.");
        }

        Id = id;
        TenantId = tenantId;
        MeetingId = meetingId;
        Sequence = sequence;
        SenderUserId = senderUserId;
        Text = Clean(text);
        SentAtUtc = sentAtUtc;
        Status = ChatMessageStatus.Visible;
    }

    public Guid Id { get; }
    public Guid TenantId { get; }
    public Guid MeetingId { get; }

    /// <summary>1..n within the meeting; lets clients ask for "everything after message n".</summary>
    public long Sequence { get; private set; }

    public Guid SenderUserId { get; }
    public string Text { get; }
    public DateTimeOffset SentAtUtc { get; }
    public ChatMessageStatus Status { get; private set; }
    public Guid? DeletedByUserId { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }

    public void AssignSequence(long sequence)
    {
        if (sequence < 1)
        {
            throw new MeetingDomainException("Message sequence starts at 1.");
        }

        Sequence = sequence;
    }

    public void Delete(Guid actorUserId, DateTimeOffset nowUtc)
    {
        if (Status == ChatMessageStatus.Deleted)
        {
            throw new MeetingDomainException("The message is already deleted.");
        }

        if (actorUserId == Guid.Empty)
        {
            throw new MeetingDomainException("The acting user is required.");
        }

        Status = ChatMessageStatus.Deleted;
        DeletedByUserId = actorUserId;
        DeletedAtUtc = nowUtc;
    }

    // Keeps line breaks and tabs, drops other control characters.
    private static string Clean(string? text)
    {
        var cleaned = new string((text ?? string.Empty).Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            throw new MeetingDomainException("A message cannot be empty.");
        }

        return cleaned.Length > MaxTextLength
            ? throw new MeetingDomainException($"A message cannot exceed {MaxTextLength} characters.")
            : cleaned;
    }
}
