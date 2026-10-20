using System.Globalization;
using KSC.MeetingManagement.Application.Participants;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;
using KSC.MeetingManagement.Domain.Notifications;

namespace KSC.MeetingManagement.Application.Notifications;

/// <summary>Organisation facts a notification needs; taken from the request in normal flows and looked up by the background worker.</summary>
public sealed record NotificationScope(Guid TenantId, string? TimeZoneId);

public sealed record GuestRecipient(Guid ParticipantId, string Address, string? Name);

/// <summary>
/// Turns business events into queued messages (Persian text). Messages are only staged in the current unit of work, so they are saved,
/// or lost, together with the change that caused them. Every message has a dedupe key so repeated triggers queue one message.
/// </summary>
public sealed class NotificationService(
    IOutboxRepository outbox,
    IParticipantRepository participants,
    ISecretProtector protector,
    INotificationDateFormatter dates,
    ITenantContext tenant,
    TimeProvider clock)
{
    /// <summary>Public base address used in links for guests, for example https://meetings.example.org. Empty means no link, only the codes.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>When false (notifications not configured) every method does nothing, so business actions never depend on notifications.</summary>
    public bool Enabled { get; set; } = true;

    public NotificationScope CurrentScope() => new(tenant.RequiredTenantId, tenant.Tenant?.TimeZoneId);

    /// <summary>An internal member was added to a meeting.</summary>
    public Task InvitedAsync(Meeting meeting, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        var scope = CurrentScope();
        return EnqueueAsync(scope, NotificationKind.MeetingInvitation, Internal(userId), $"invite:{meeting.Id}:{userId}",
            "دعوت به جلسه", $"شما به جلسهٔ «{meeting.Title}» دعوت شده‌اید.{When(meeting, scope)}{Number(meeting)}", cancellationToken);
    }

    /// <summary>A guest was invited. The raw token is part of the (protected) text and is never stored anywhere else.</summary>
    public Task GuestInvitedAsync(Meeting meeting, GuestRecipient guest, string rawToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        ArgumentNullException.ThrowIfNull(guest);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        var scope = CurrentScope();
        var link = string.IsNullOrWhiteSpace(PublicBaseUrl)
            ? string.Empty
            : $"\nپیوند ورود: {PublicBaseUrl.TrimEnd('/')}/join/{meeting.Id}/{guest.ParticipantId}?t={Uri.EscapeDataString(rawToken)}";
        var body = $"شما به جلسهٔ «{meeting.Title}» دعوت شده‌اید.{When(meeting, scope)}{link}\nشناسهٔ شرکت‌کننده: {guest.ParticipantId}\nکد دعوت: {rawToken}\nاین کد شخصی است؛ آن را با دیگران به اشتراک نگذارید.";
        return EnqueueAsync(scope, NotificationKind.MeetingInvitation, Guest(guest), $"guest-invite:{guest.ParticipantId}", "دعوت به جلسه", body, cancellationToken);
    }

    /// <summary>The time of a meeting changed. Sent to everyone who is invited except the person who made the change.</summary>
    public async Task MeetingChangedAsync(Meeting meeting, Guid? exceptUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        var scope = CurrentScope();
        foreach (var recipient in RecipientsOf(meeting, exceptUserId, await participants.ListAsync(meeting.Id, cancellationToken)))
        {
            await EnqueueAsync(scope, NotificationKind.MeetingChanged, recipient, $"changed:{meeting.Id}:{meeting.Version}:{recipient.Key}",
                "تغییر در جلسه", $"زمان یا مشخصات جلسهٔ «{meeting.Title}» تغییر کرد.{When(meeting, scope)}{Number(meeting)}", cancellationToken);
        }
    }

    public async Task MeetingCancelledAsync(Meeting meeting, Guid? exceptUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        var scope = CurrentScope();
        var reason = string.IsNullOrWhiteSpace(meeting.CancellationReason) ? string.Empty : $"\nدلیل: {meeting.CancellationReason}";
        foreach (var recipient in RecipientsOf(meeting, exceptUserId, await participants.ListAsync(meeting.Id, cancellationToken)))
        {
            await EnqueueAsync(scope, NotificationKind.MeetingCancelled, recipient, $"cancelled:{meeting.Id}:{recipient.Key}",
                "لغو جلسه", $"جلسهٔ «{meeting.Title}» لغو شد.{Number(meeting)}{reason}", cancellationToken);
        }
    }

    /// <summary>Reminder before the meeting. The label (for example "24h") and the start time are part of the key: a moved meeting is reminded again.</summary>
    public async Task MeetingReminderAsync(
        NotificationScope scope, Meeting meeting, IReadOnlyList<MeetingParticipant> meetingParticipants, string label, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(meeting);
        ArgumentNullException.ThrowIfNull(meetingParticipants);
        foreach (var recipient in RecipientsOf(meeting, exceptUserId: null, meetingParticipants))
        {
            await EnqueueAsync(scope, NotificationKind.MeetingReminder, recipient,
                $"reminder:{meeting.Id}:{meeting.StartUtc?.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{label}:{recipient.Key}",
                "یادآوری جلسه", $"یادآوری: جلسهٔ «{meeting.Title}» نزدیک است.{When(meeting, scope)}", cancellationToken);
        }
    }

    public Task ActionAssignedAsync(ActionItem action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var scope = CurrentScope();
        return EnqueueAsync(scope, NotificationKind.ActionAssigned, Internal(action.OwnerUserId), $"action-assigned:{action.Id}:{action.OwnerUserId}",
            "اقدام جدید", $"اقدام «{action.Title}» به شما واگذار شد. موعد: {action.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.", cancellationToken);
    }

    /// <summary>Reminder about an action: "due-soon" once per due day, "overdue" once per calendar day of the organisation.</summary>
    public Task ActionDueAsync(NotificationScope scope, ActionItem action, string label, DateOnly today, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(action);

        var text = label == "overdue"
            ? $"اقدام «{action.Title}» از موعد خود ({action.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}) گذشته است."
            : $"موعد اقدام «{action.Title}» نزدیک است ({action.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}).";
        var key = label == "overdue"
            ? $"action-overdue:{action.Id}:{today:yyyyMMdd}"
            : $"action-due-soon:{action.Id}:{action.DueDate:yyyyMMdd}";
        return EnqueueAsync(scope, NotificationKind.ActionDue, Internal(action.OwnerUserId), key, "موعد اقدام", text, cancellationToken);
    }

    private List<Recipient> RecipientsOf(Meeting meeting, Guid? exceptUserId, IReadOnlyList<MeetingParticipant> meetingParticipants)
    {
        var recipients = new List<Recipient>();
        var now = clock.GetUtcNow();
        if (meeting.OrganizerId != exceptUserId)
        {
            recipients.Add(Internal(meeting.OrganizerId));
        }

        foreach (var p in meetingParticipants)
        {
            if (p.Status is InvitationStatus.Revoked or InvitationStatus.Declined)
            {
                continue;
            }

            if (p.Kind == ParticipantKind.Internal && p.UserId is { } userId && userId != exceptUserId && recipients.All(r => r.UserId != userId))
            {
                recipients.Add(Internal(userId));
            }
            else if (p.Kind == ParticipantKind.Guest && p.GuestEmail is { } email && !p.IsExpired(now))
            {
                recipients.Add(Guest(new GuestRecipient(p.Id, email, p.GuestName)));
            }
        }

        return recipients;
    }

    private async Task EnqueueAsync(
        NotificationScope scope, NotificationKind kind, Recipient recipient, string dedupeKey, string subject, string body, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return;
        }

        var message = new OutboxMessage(
            Guid.NewGuid(), scope.TenantId, kind, recipient.Type, recipient.UserId, recipient.Address, recipient.Name,
            subject, protector.Protect(body), dedupeKey, clock.GetUtcNow(), clock.GetUtcNow());
        await outbox.AddAsync(message, cancellationToken);
    }

    private string When(Meeting meeting, NotificationScope scope) =>
        meeting.StartUtc is { } start ? $" زمان: {dates.Format(start, scope.TimeZoneId)}." : string.Empty;

    private static string Number(Meeting meeting) => meeting.Number is { } n ? $" شمارهٔ جلسه: {n}." : string.Empty;

    private static Recipient Internal(Guid userId) => new(RecipientType.Internal, userId, null, null, $"u{userId:N}");

    private static Recipient Guest(GuestRecipient guest) => new(RecipientType.Guest, null, guest.Address, guest.Name, $"g{guest.ParticipantId:N}");

    private sealed record Recipient(RecipientType Type, Guid? UserId, string? Address, string? Name, string Key);
}
