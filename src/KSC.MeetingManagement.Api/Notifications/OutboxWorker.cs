using KSC.MeetingManagement.Application.Notifications;
using KSC.MeetingManagement.Calendar;
using KSC.MeetingManagement.Infrastructure.Notifications;

namespace KSC.MeetingManagement.Api.Notifications;

/// <summary>Writes dates for notifications in the Jalali calendar and the organisation's time zone.</summary>
public sealed class JalaliNotificationDateFormatter : INotificationDateFormatter
{
    public string Format(DateTimeOffset instantUtc, string? timeZoneId)
    {
        var zone = TimeZoneInfo.Utc;
        if (!string.IsNullOrWhiteSpace(timeZoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var found))
        {
            zone = found;
        }

        var local = TimeZoneInfo.ConvertTime(instantUtc, zone);
        try
        {
            var date = KscJalaliCalendarService.Instance.FromGregorian(DateOnly.FromDateTime(local.DateTime));
            return $"{PersianText.FormatLong(date, KscJalaliCalendarService.Instance)} ساعت {PersianText.Time(TimeOnly.FromDateTime(local.DateTime))}";
        }
        catch (ArgumentOutOfRangeException)
        {
            // Outside the supported Jalali years: fall back to an unambiguous plain date.
            return local.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>
/// Background loop: delivers queued notifications and, once a minute, queues reminders. Each pass uses a fresh scope, and a failing pass is
/// logged and retried on the next tick, so one bad message or a short database outage never stops the loop.
/// </summary>
public sealed partial class OutboxWorker(IServiceScopeFactory scopes, NotificationRuntime runtime, TimeProvider clock, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!runtime.Enabled)
        {
            LogDisabled();
            return;
        }

        var nextReminders = clock.GetUtcNow();
        using var timer = new PeriodicTimer(runtime.PollInterval);
        do
        {
            await RunOnceAsync(runReminders: clock.GetUtcNow() >= nextReminders, stoppingToken);
            if (clock.GetUtcNow() >= nextReminders)
            {
                nextReminders = clock.GetUtcNow() + runtime.ReminderInterval;
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>One pass: optionally queue reminders, then deliver what is due. Public so tests and operators can trigger it.</summary>
    public async Task RunOnceAsync(bool runReminders, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (runReminders)
            {
                var queued = await scope.ServiceProvider.GetRequiredService<ReminderService>().RunAsync(cancellationToken);
                if (queued > 0)
                {
                    LogReminders(queued);
                }
            }

            var result = await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(cancellationToken);
            if (result.Claimed > 0)
            {
                LogBatch(result.Claimed, result.Sent, result.Retrying, result.Dead, result.Skipped);
            }

            await PublishQueueSizeAsync(scope.ServiceProvider, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPassFailed(ex);
        }
    }

    /// <summary>Exports the queue size (all organisations) as metrics, so a growing backlog or dead letters can raise an alert.</summary>
    private async Task PublishQueueSizeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (services.GetService<IOutboxAdminStore>() is { } store)
        {
            var stats = await store.StatsAsync(null, clock.GetUtcNow(), cancellationToken);
            KSC.MeetingManagement.Application.Telemetry.OutboxQueueMetrics.Update(stats.Pending, stats.Dead, stats.OldestPendingAgeSeconds ?? 0);
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notifications are disabled: set Notifications:PayloadKey (base64, 32 bytes) to enable the outbox worker")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued {Count} reminders")]
    private partial void LogReminders(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox batch: claimed {Claimed}, sent {Sent}, retrying {Retrying}, dead {Dead}, skipped {Skipped}")]
    private partial void LogBatch(int claimed, int sent, int retrying, int dead, int skipped);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox pass failed; it will be retried on the next tick")]
    private partial void LogPassFailed(Exception exception);
}
