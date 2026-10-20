namespace KSC.MeetingManagement.Domain.Notifications;

public enum NotificationKind
{
    MeetingInvitation = 1,
    MeetingChanged = 2,
    MeetingCancelled = 3,
    MeetingReminder = 4,
    ActionAssigned = 5,
    ActionDue = 6,
}

public enum RecipientType
{
    Internal = 1,
    Guest = 2,
}

public enum OutboxStatus
{
    /// <summary>Waiting for (another) delivery attempt.</summary>
    Pending = 1,

    /// <summary>Delivered.</summary>
    Sent = 2,

    /// <summary>Gave up after the maximum attempts; kept for inspection.</summary>
    Dead = 3,

    /// <summary>Nothing to send to (for example no contact address); not an error to retry.</summary>
    Skipped = 4,
}

/// <summary>
/// A notification waiting to be delivered. It is written in the same transaction as the business change that caused it,
/// so a temporary failure of the channel never loses it. <see cref="DedupeKey"/> is unique per tenant, so the same event
/// cannot be queued twice. A claim (<see cref="LockedUntilUtc"/>) keeps several workers from sending the same message.
/// The text is stored protected because it can contain a guest's personal link.
/// </summary>
public sealed class OutboxMessage
{
    public const int MaxErrorLength = 300;

    // Used by EF Core when materialising stored messages.
    private OutboxMessage()
    {
        Subject = string.Empty;
        ProtectedBody = string.Empty;
        DedupeKey = string.Empty;
    }

    public OutboxMessage(
        Guid id,
        Guid tenantId,
        NotificationKind kind,
        RecipientType recipientType,
        Guid? recipientUserId,
        string? recipientAddress,
        string? recipientName,
        string subject,
        string protectedBody,
        string dedupeKey,
        DateTimeOffset createdAtUtc,
        DateTimeOffset notBeforeUtc)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("Message and tenant ids are required.");
        }

        if (recipientType == RecipientType.Internal && (recipientUserId is null || recipientUserId == Guid.Empty))
        {
            throw new ArgumentException("An internal recipient needs a user id.", nameof(recipientUserId));
        }

        if (recipientType == RecipientType.Guest && string.IsNullOrWhiteSpace(recipientAddress))
        {
            throw new ArgumentException("A guest recipient needs an address.", nameof(recipientAddress));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedBody);
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupeKey);

        Id = id;
        TenantId = tenantId;
        Kind = kind;
        RecipientType = recipientType;
        RecipientUserId = recipientUserId;
        RecipientAddress = recipientAddress?.Trim();
        RecipientName = recipientName?.Trim();
        Subject = subject.Trim();
        ProtectedBody = protectedBody;
        DedupeKey = dedupeKey;
        CreatedAtUtc = createdAtUtc;
        NextAttemptAtUtc = notBeforeUtc;
        Status = OutboxStatus.Pending;
    }

    public Guid Id { get; }
    public Guid TenantId { get; }
    public NotificationKind Kind { get; }
    public RecipientType RecipientType { get; }
    public Guid? RecipientUserId { get; }
    public string? RecipientAddress { get; }
    public string? RecipientName { get; }
    public string Subject { get; }
    public string ProtectedBody { get; }
    public string DedupeKey { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset NextAttemptAtUtc { get; private set; }
    public int Attempts { get; private set; }
    public OutboxStatus Status { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }
    public DateTimeOffset? SentAtUtc { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Concurrency token: two workers cannot both claim the same message.</summary>
    public long ClaimVersion { get; private set; }

    public bool IsDue(DateTimeOffset nowUtc) =>
        Status == OutboxStatus.Pending && NextAttemptAtUtc <= nowUtc && (LockedUntilUtc is null || LockedUntilUtc <= nowUtc);

    /// <summary>Takes the message for one delivery attempt. Returns false when it is not due or somebody else holds it.</summary>
    public bool TryClaim(DateTimeOffset nowUtc, TimeSpan lease)
    {
        if (!IsDue(nowUtc))
        {
            return false;
        }

        LockedUntilUtc = nowUtc + lease;
        ClaimVersion++;
        return true;
    }

    public void MarkSent(DateTimeOffset nowUtc)
    {
        Attempts++;
        Status = OutboxStatus.Sent;
        SentAtUtc = nowUtc;
        LockedUntilUtc = null;
        LastError = null;
        ClaimVersion++;
    }

    public void MarkSkipped(string reason)
    {
        Status = OutboxStatus.Skipped;
        LockedUntilUtc = null;
        LastError = Trim(reason);
        ClaimVersion++;
    }

    /// <summary>Records a failed attempt and schedules the next one after <paramref name="retryDelay"/>, or gives up after <paramref name="maxAttempts"/>.</summary>
    public void MarkFailed(DateTimeOffset nowUtc, string error, int maxAttempts, TimeSpan retryDelay)
    {
        Attempts++;
        LastError = Trim(error);
        LockedUntilUtc = null;
        ClaimVersion++;

        if (Attempts >= maxAttempts)
        {
            Status = OutboxStatus.Dead;
            return;
        }

        NextAttemptAtUtc = nowUtc + retryDelay;
    }

    /// <summary>Puts a dead message back in the queue (operator action after fixing the cause).</summary>
    public void Requeue(DateTimeOffset nowUtc)
    {
        if (Status != OutboxStatus.Dead)
        {
            throw new InvalidOperationException("Only a dead message can be requeued.");
        }

        Status = OutboxStatus.Pending;
        Attempts = 0;
        NextAttemptAtUtc = nowUtc;
        LockedUntilUtc = null;
        ClaimVersion++;
    }

    private static string Trim(string text) => text.Length > MaxErrorLength ? text[..MaxErrorLength] : text;
}
