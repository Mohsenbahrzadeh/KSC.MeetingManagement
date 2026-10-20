using KSC.MeetingManagement.Domain.Notifications;

namespace KSC.MeetingManagement.Application.Notifications;

public sealed record OutboxRunResult(int Claimed, int Sent, int Retrying, int Dead, int Skipped);

/// <summary>
/// Delivers queued messages. Each message is saved right after its attempt, so a crash can at worst repeat the attempt in progress
/// (delivery is at-least-once) but never forgets a message. Temporary failures are retried with growing delays until the attempt limit.
/// </summary>
public sealed class OutboxProcessor(
    IOutboxRepository outbox,
    IContactDirectory contacts,
    INotificationChannel channel,
    ISecretProtector protector,
    NotificationOptions options,
    TimeProvider clock)
{
    public async Task<OutboxRunResult> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var batch = await outbox.ClaimDueAsync(clock.GetUtcNow(), options.Lease, options.BatchSize, cancellationToken);
        int sent = 0, retrying = 0, dead = 0, skipped = 0;

        foreach (var message in batch)
        {
            switch (await DeliverAsync(message, cancellationToken))
            {
                case OutboxStatus.Sent:
                    sent++;
                    break;
                case OutboxStatus.Dead:
                    dead++;
                    break;
                case OutboxStatus.Skipped:
                    skipped++;
                    break;
                default:
                    retrying++;
                    break;
            }

            await outbox.SaveChangesAsync(cancellationToken);
        }

        return new OutboxRunResult(batch.Count, sent, retrying, dead, skipped);
    }

    private async Task<OutboxStatus> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var address = message.RecipientAddress;
        var name = message.RecipientName;
        if (message.RecipientType == RecipientType.Internal)
        {
            Contact? contact;
            try
            {
                contact = await contacts.FindAsync(message.TenantId, message.RecipientUserId!.Value, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A broken directory is a temporary condition, not "no address": retry later.
                message.MarkFailed(clock.GetUtcNow(), "contact directory failed: " + ex.GetType().Name, options.MaxAttempts, options.Delay(message.Attempts + 1));
                return message.Status;
            }

            if (contact is null)
            {
                message.MarkSkipped("no contact address for the recipient");
                return message.Status;
            }

            (address, name) = (contact.Address, contact.DisplayName ?? name);
        }

        string body;
        try
        {
            body = protector.Unprotect(message.ProtectedBody);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            message.MarkFailed(clock.GetUtcNow(), "message text cannot be read", maxAttempts: 1, TimeSpan.Zero);
            return message.Status;
        }

        DeliveryResult result;
        try
        {
            result = await channel.SendAsync(new OutgoingNotification(message.Id, message.Kind, address!, name, message.Subject, body), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the exception type is kept: messages can contain addresses or personal text.
            result = DeliveryResult.Temporary("channel error: " + ex.GetType().Name);
        }

        var now = clock.GetUtcNow();
        switch (result.Outcome)
        {
            case DeliveryOutcome.Delivered:
                message.MarkSent(now);
                break;
            case DeliveryOutcome.PermanentFailure:
                message.MarkFailed(now, result.Error ?? "permanent failure", maxAttempts: 1, TimeSpan.Zero);
                break;
            default:
                message.MarkFailed(now, result.Error ?? "temporary failure", options.MaxAttempts, options.Delay(message.Attempts + 1));
                break;
        }

        return message.Status;
    }
}
