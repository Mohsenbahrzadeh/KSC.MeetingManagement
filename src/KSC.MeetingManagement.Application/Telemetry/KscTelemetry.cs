using System.Diagnostics.Metrics;

namespace KSC.MeetingManagement.Application.Telemetry;

/// <summary>
/// Application metrics. Instruments are cheap no-ops until something (the Prometheus endpoint or OpenTelemetry) listens.
/// Tags are small fixed sets (outcome, policy); never user, tenant or meeting ids.
/// </summary>
public static class KscTelemetry
{
    public const string MeterName = "KSC.MeetingManagement";

    private static readonly Meter AppMeter = new(MeterName, "1.0");

    public static Counter<long> RoomJoins { get; } =
        AppMeter.CreateCounter<long>("ksc.room.joins", description: "Requests to enter a meeting room, by outcome.");

    public static Counter<long> RateLimited { get; } =
        AppMeter.CreateCounter<long>("ksc.requests.rate_limited", description: "Requests refused by a rate limit.");

    public static Counter<long> AuditFailures { get; } =
        AppMeter.CreateCounter<long>("ksc.audit.failures", description: "Audit events that could not be written.");

    public static Counter<long> RoomControlFailures { get; } =
        AppMeter.CreateCounter<long>("ksc.room.control_failures", description: "Failed calls to the media server (close room, remove participant).");

    public static Counter<long> AuthorizationDenied { get; } =
        AppMeter.CreateCounter<long>("ksc.authorization.denied", description: "Requests refused for lack of permission.");
}

/// <summary>
/// Size of the notification queue across all organisations, refreshed by the outbox worker after every pass.
/// Nothing is reported until the first refresh (for example when notifications are disabled), so an absent series means "not measured".
/// </summary>
public static class OutboxQueueMetrics
{
    private static readonly Meter QueueMeter = new(KscTelemetry.MeterName, "1.0");
    private static volatile Snapshot? _current;

    private sealed record Snapshot(int Pending, int Dead, double OldestPendingAgeSeconds);

    static OutboxQueueMetrics()
    {
        QueueMeter.CreateObservableGauge<long>("ksc.outbox.pending", () => Measure(s => (long)s.Pending), description: "Notifications waiting to be delivered.");
        QueueMeter.CreateObservableGauge<long>("ksc.outbox.dead", () => Measure(s => (long)s.Dead), description: "Notifications that were given up on and wait for an operator.");
        QueueMeter.CreateObservableGauge<double>("ksc.outbox.oldest_pending_age_seconds", () => Measure(s => s.OldestPendingAgeSeconds), unit: "s", description: "Age of the oldest waiting notification.");
    }

    private static IEnumerable<Measurement<T>> Measure<T>(Func<Snapshot, T> pick)
        where T : struct =>
        _current is { } s ? [new Measurement<T>(pick(s))] : [];

    public static void Update(int pending, int dead, double oldestPendingAgeSeconds) =>
        _current = new Snapshot(pending, dead, oldestPendingAgeSeconds);

    /// <summary>For tests.</summary>
    public static void Reset() => _current = null;
}
