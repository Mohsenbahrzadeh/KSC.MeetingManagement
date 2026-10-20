using KSC.MeetingManagement.Application.Actions;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Notifications;

/// <summary>Cross-tenant read access used only by the background reminder run (it has no request and no tenant of its own).</summary>
public interface IReminderSource
{
    /// <summary>Scheduled meetings of every tenant starting in (fromUtc, toUtc].</summary>
    Task<IReadOnlyList<Meeting>> MeetingsStartingBetweenAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);

    /// <summary>Active actions of every tenant whose due day is on or before the given day (a superset; callers apply each tenant's own today).</summary>
    /// <summary>Participants (internal and guests) of the given meetings, of any tenant.</summary>
    Task<IReadOnlyList<MeetingParticipant>> ParticipantsOfAsync(IReadOnlyCollection<Guid> meetingIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<ActionItem>> ActiveActionsDueUntilAsync(DateOnly dueUntil, CancellationToken cancellationToken);
}

/// <summary>
/// Queues reminders for meetings that are about to start and for actions that are due soon or overdue. Safe to run as often as wanted:
/// every reminder has a dedupe key, so a repeated run adds nothing.
/// </summary>
public sealed class ReminderService(
    IReminderSource source,
    NotificationService notifications,
    IOutboxRepository outbox,
    ITenantStore tenants,
    TimeProvider clock)
{
    public static readonly TimeSpan FirstMeetingReminder = TimeSpan.FromHours(24);
    public static readonly TimeSpan SecondMeetingReminder = TimeSpan.FromHours(1);

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var queued = 0;
        var zones = new Dictionary<Guid, string?>();

        async Task<NotificationScope> ScopeOf(Guid tenantId)
        {
            if (!zones.TryGetValue(tenantId, out var zone))
            {
                zone = (await tenants.FindByIdAsync(tenantId, cancellationToken))?.TimeZoneId;
                zones[tenantId] = zone;
            }

            return new NotificationScope(tenantId, zone);
        }

        var upcoming = await source.MeetingsStartingBetweenAsync(now, now + FirstMeetingReminder, cancellationToken);
        var allParticipants = await source.ParticipantsOfAsync(upcoming.Select(m => m.Id).ToList(), cancellationToken);
        foreach (var meeting in upcoming)
        {
            if (meeting.StartUtc is not { } start)
            {
                continue;
            }

            var label = start - now <= SecondMeetingReminder ? "1h" : "24h";
            await notifications.MeetingReminderAsync(
                await ScopeOf(meeting.TenantId), meeting, allParticipants.Where(p => p.MeetingId == meeting.Id).ToList(), label, cancellationToken);
            queued++;
        }

        // An action whose due day is tomorrow or earlier can matter today in some time zone, so fetch a day beyond UTC today.
        var horizon = DateOnly.FromDateTime(now.UtcDateTime).AddDays(2);
        foreach (var action in await source.ActiveActionsDueUntilAsync(horizon, cancellationToken))
        {
            var scope = await ScopeOf(action.TenantId);
            var today = TenantClock.Today(scope.TimeZoneId, clock);
            if (action.DueDate < today)
            {
                await notifications.ActionDueAsync(scope, action, "overdue", today, cancellationToken);
                queued++;
            }
            else if (action.DueDate <= today.AddDays(1))
            {
                await notifications.ActionDueAsync(scope, action, "due-soon", today, cancellationToken);
                queued++;
            }
        }

        await outbox.SaveChangesAsync(cancellationToken);
        return queued;
    }
}
