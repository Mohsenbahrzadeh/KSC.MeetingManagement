using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using KSC.MeetingManagement.Application.Actions;
using KSC.MeetingManagement.Application.Notifications;
using KSC.MeetingManagement.Domain.Meetings;
using KSC.MeetingManagement.Domain.Notifications;
using KSC.MeetingManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KSC.MeetingManagement.Infrastructure.Notifications;

/// <summary>AES-256-GCM. The stored text is base64(nonce | tag | ciphertext); any change to it is detected when reading.</summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Context = Encoding.UTF8.GetBytes("ksc-outbox-v1");

    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
        {
            throw new ArgumentException("The key must be 32 bytes.", nameof(key));
        }

        _key = key;
    }

    /// <summary>Reads a base64 key of 32 bytes from configuration text. Returns null when it is missing or invalid.</summary>
    public static AesGcmSecretProtector? TryCreate(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            return null;
        }

        try
        {
            var key = Convert.FromBase64String(base64Key.Trim());
            return key.Length == 32 ? new AesGcmSecretProtector(key) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public string Protect(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);

        var plain = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag, Context);

        var result = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSize);
        cipher.CopyTo(result, NonceSize + TagSize);
        return Convert.ToBase64String(result);
    }

    public string Unprotect(string protectedText)
    {
        ArgumentNullException.ThrowIfNull(protectedText);

        var data = Convert.FromBase64String(protectedText);
        if (data.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The protected text is too short.");
        }

        var nonce = data.AsSpan(0, NonceSize);
        var tag = data.AsSpan(NonceSize, TagSize);
        var cipher = data.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain, Context);
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>Used while notifications are not configured: nothing is queued and nothing is sent.</summary>
public sealed class DisabledOutboxRepository : IOutboxRepository
{
    public Task<bool> AddAsync(OutboxMessage message, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(DateTimeOffset nowUtc, TimeSpan lease, int batchSize, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OutboxMessage>>([]);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class DisabledSecretProtector : ISecretProtector
{
    public string Protect(string plainText) => throw new InvalidOperationException("Notifications are not configured (Notifications:PayloadKey).");

    public string Unprotect(string protectedText) => throw new InvalidOperationException("Notifications are not configured (Notifications:PayloadKey).");
}

/// <summary>No directory exists until sign-in is built; members without a known address are skipped, not failed.</summary>
public sealed class NullContactDirectory : IContactDirectory
{
    public Task<Contact?> FindAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) => Task.FromResult<Contact?>(null);
}

/// <summary>Writes only the kind and message id to the log (never addresses or text). Default channel until a real one is configured.</summary>
public sealed partial class LoggingNotificationChannel(ILogger<LoggingNotificationChannel> logger) : INotificationChannel
{
    public Task<DeliveryResult> SendAsync(OutgoingNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        LogSent(notification.Kind, notification.MessageId);
        return Task.FromResult(DeliveryResult.Delivered);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {Kind} {MessageId} handed to the logging channel (not delivered to a person)")]
    private partial void LogSent(NotificationKind kind, Guid messageId);
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public string? User { get; set; }

    public string? Password { get; set; }

    public string? From { get; set; }

    public bool EnableSsl { get; set; } = true;
}

/// <summary>Email through an SMTP server. Server rejections (5xx) are permanent; connection problems and 4xx are temporary.</summary>
public sealed class SmtpNotificationChannel(SmtpOptions options) : INotificationChannel
{
    public async Task<DeliveryResult> SendAsync(OutgoingNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        try
        {
            using var client = new SmtpClient(options.Host, options.Port)
            {
                EnableSsl = options.EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Credentials = string.IsNullOrWhiteSpace(options.User) ? null : new NetworkCredential(options.User, options.Password),
            };
            using var mail = new MailMessage
            {
                From = new MailAddress(options.From!),
                Subject = notification.Subject,
                Body = notification.Body,
                BodyEncoding = Encoding.UTF8,
                SubjectEncoding = Encoding.UTF8,
            };
            mail.To.Add(new MailAddress(notification.ToAddress, notification.ToName, Encoding.UTF8));
            await client.SendMailAsync(mail, cancellationToken);
            return DeliveryResult.Delivered;
        }
        catch (FormatException)
        {
            return DeliveryResult.Permanent("invalid address");
        }
        catch (SmtpFailedRecipientException ex) when ((int)ex.StatusCode >= 500)
        {
            return DeliveryResult.Permanent("recipient rejected");
        }
        catch (SmtpException ex)
        {
            return DeliveryResult.Temporary("smtp error " + ex.StatusCode);
        }
    }
}

public sealed class EfOutboxRepository(KscDbContext db) : IOutboxRepository
{
    public async Task<bool> AddAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var key = message.DedupeKey;
        if (db.OutboxMessages.Local.Any(m => m.TenantId == message.TenantId && m.DedupeKey == key)
            || await db.OutboxMessages.IgnoreQueryFilters().AnyAsync(m => m.TenantId == message.TenantId && m.DedupeKey == key, cancellationToken))
        {
            return false;
        }

        db.OutboxMessages.Add(message);
        return true;
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(DateTimeOffset nowUtc, TimeSpan lease, int batchSize, CancellationToken cancellationToken)
    {
        // The worker has no tenant, so tenant filters are bypassed here on purpose; it only claims and delivers, it never exposes data.
        var candidates = await db.OutboxMessages.IgnoreQueryFilters()
            .Where(m => m.Status == OutboxStatus.Pending && m.NextAttemptAtUtc <= nowUtc && (m.LockedUntilUtc == null || m.LockedUntilUtc <= nowUtc))
            .OrderBy(m => m.NextAttemptAtUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var claimed = new List<OutboxMessage>();
        foreach (var message in candidates)
        {
            if (!message.TryClaim(nowUtc, lease))
            {
                continue;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                claimed.Add(message);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another worker claimed it first.
                db.Entry(message).State = EntityState.Detached;
            }
        }

        return claimed;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

public sealed class EfReminderSource(KscDbContext db) : IReminderSource
{
    public async Task<IReadOnlyList<Meeting>> MeetingsStartingBetweenAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken) =>
        await db.Meetings.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.Status == MeetingStatus.Scheduled && m.StartUtc != null && m.StartUtc > fromUtc && m.StartUtc <= toUtc)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MeetingParticipant>> ParticipantsOfAsync(IReadOnlyCollection<Guid> meetingIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meetingIds);
        var ids = meetingIds.ToList();
        return await db.Participants.IgnoreQueryFilters().AsNoTracking().Where(p => ids.Contains(p.MeetingId)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ActionItem>> ActiveActionsDueUntilAsync(DateOnly dueUntil, CancellationToken cancellationToken) =>
        await db.ActionItems.IgnoreQueryFilters().AsNoTracking()
            .Where(a => (a.Status == ActionItemStatus.Open || a.Status == ActionItemStatus.InProgress) && a.DueDate <= dueUntil)
            .ToListAsync(cancellationToken);
}
