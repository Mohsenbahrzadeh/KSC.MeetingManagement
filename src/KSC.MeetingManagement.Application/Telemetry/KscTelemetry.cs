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
