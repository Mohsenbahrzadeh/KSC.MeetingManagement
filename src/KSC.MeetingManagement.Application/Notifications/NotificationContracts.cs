using KSC.MeetingManagement.Domain.Notifications;

namespace KSC.MeetingManagement.Application.Notifications;

/// <summary>A message ready to hand to a delivery channel.</summary>
public sealed record OutgoingNotification(
    Guid MessageId,
    NotificationKind Kind,
    string ToAddress,
    string? ToName,
    string Subject,
    string Body);

/// <summary>How a delivery attempt ended. Temporary failures are retried, permanent ones are not.</summary>
public enum DeliveryOutcome
{
    Delivered = 1,
    TemporaryFailure = 2,
    PermanentFailure = 3,
}

public sealed record DeliveryResult(DeliveryOutcome Outcome, string? Error = null)
{
    public static DeliveryResult Delivered { get; } = new(DeliveryOutcome.Delivered);

    public static DeliveryResult Temporary(string error) => new(DeliveryOutcome.TemporaryFailure, error);

    public static DeliveryResult Permanent(string error) => new(DeliveryOutcome.PermanentFailure, error);
}

/// <summary>A way to reach people (email, SMS, messenger). Implementations must not throw for delivery problems; they return a result.</summary>
public interface INotificationChannel
{
    Task<DeliveryResult> SendAsync(OutgoingNotification notification, CancellationToken cancellationToken);
}

/// <summary>Finds how to reach an organisation member. Returns null when no address is known.</summary>
public interface IContactDirectory
{
    Task<Contact?> FindAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);
}

public sealed record Contact(string Address, string? DisplayName);

/// <summary>Protects text at rest (messages can carry a guest's personal link).</summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    string Unprotect(string protectedText);
}

/// <summary>Formats an instant for people (Jalali date, local time). Kept as a port so the application does not depend on the UI calendar library.</summary>
public interface INotificationDateFormatter
{
    string Format(DateTimeOffset instantUtc, string? timeZoneId);
}

/// <summary>
/// Storage of the outbox. <see cref="Add"/> only stages the message in the current unit of work, so it is saved together with the
/// business change that caused it. Claiming is tenant-wide (used by the background worker, which runs outside a request).
/// </summary>
public interface IOutboxRepository
{
    /// <summary>Stages a message. Returns false (and stages nothing) when a message with the same dedupe key is already queued.</summary>
    Task<bool> AddAsync(OutboxMessage message, CancellationToken cancellationToken);

    /// <summary>Messages of every tenant that are due now, oldest first, already claimed for the lease. Safe to call from several workers.</summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(DateTimeOffset nowUtc, TimeSpan lease, int batchSize, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class NotificationOptions
{
    public int MaxAttempts { get; set; } = 6;

    public int BatchSize { get; set; } = 20;

    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Delay before attempt n+1: BaseDelay * 2^(n-1), at most MaxDelay.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan Delay(int failedAttempts)
    {
        var exponent = Math.Clamp(failedAttempts - 1, 0, 20);
        var delay = TimeSpan.FromTicks(BaseDelay.Ticks * (1L << exponent));
        return delay > MaxDelay ? MaxDelay : delay;
    }
}
