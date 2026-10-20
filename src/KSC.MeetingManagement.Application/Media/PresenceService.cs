using System.Text.RegularExpressions;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Media;

public enum MediaEventType
{
    ParticipantJoined = 1,
    ParticipantLeft = 2,
    RoomFinished = 3,
}

/// <summary>A media-server event that was already authenticated (signature and body hash verified) by the caller.</summary>
public sealed record MediaWebhookEvent(string EventId, MediaEventType Type, string Room, string? Identity, string? DisplayName, DateTimeOffset AtUtc);

public enum WebhookOutcome
{
    Recorded = 0,
    Duplicate,
    Ignored,
}

public sealed record PresenceDto(string Identity, string DisplayName, DateTimeOffset JoinedAtUtc, DateTimeOffset? LeftAtUtc, double Minutes);

public sealed record PresenceSummary(int People, double TotalMinutes, IReadOnlyList<PresenceDto> Stays);

/// <summary>Persistence port for presence. Implementations only return data of the tenant set on the current scope.</summary>
public interface IPresenceRepository
{
    Task<bool> HasProcessedAsync(string eventId, CancellationToken cancellationToken);

    Task MarkProcessedAsync(ProcessedMediaEvent processed, CancellationToken cancellationToken);

    Task<PresenceRecord?> FindOpenAsync(Guid meetingId, string identity, CancellationToken cancellationToken);

    Task<IReadOnlyList<PresenceRecord>> ListOpenAsync(Guid meetingId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PresenceRecord>> ListAsync(Guid meetingId, CancellationToken cancellationToken);

    Task AddAsync(PresenceRecord record, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Turns validated media events into attendance. The room name carries tenant and meeting, so the tenant is set from it before anything is
/// stored. Repeated events (same id) change nothing, a second "joined" for someone already inside is ignored, and a "left" without a
/// known stay is ignored: presence only ever comes from events that fit together.
/// </summary>
public sealed partial class PresenceService(
    IPresenceRepository presence,
    IMeetingRepository meetings,
    ITenantStore tenants,
    ITenantContextSetter tenantSetter,
    ITenantContext tenant,
    TimeProvider clock)
{
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    [GeneratedRegex("^ksc-(?<t>[0-9a-f]{32})-(?<m>[0-9a-f]{32})$", RegexOptions.CultureInvariant)]
    private static partial Regex RoomPattern();

    [GeneratedRegex("^[ug]-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPattern();

    /// <summary>Splits a room name made by <see cref="MediaAccessService.RoomName"/>; false for any other name.</summary>
    public static bool TryParseRoom(string? room, out Guid tenantId, out Guid meetingId)
    {
        tenantId = meetingId = Guid.Empty;
        if (room is null || RoomPattern().Match(room) is not { Success: true } m)
        {
            return false;
        }

        return Guid.TryParseExact(m.Groups["t"].Value, "N", out tenantId) && Guid.TryParseExact(m.Groups["m"].Value, "N", out meetingId);
    }

    public async Task<WebhookOutcome> HandleAsync(MediaWebhookEvent evt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!TryParseRoom(evt.Room, out var tenantId, out var meetingId)
            || string.IsNullOrWhiteSpace(evt.EventId)
            || evt.Type != MediaEventType.RoomFinished && (evt.Identity is null || !IdentityPattern().IsMatch(evt.Identity))
            || await tenants.FindByIdAsync(tenantId, cancellationToken) is not { } found)
        {
            return WebhookOutcome.Ignored;
        }

        tenantSetter.SetTenant(found);
        if (await meetings.GetAsync(meetingId, cancellationToken) is null)
        {
            return WebhookOutcome.Ignored;
        }

        if (await presence.HasProcessedAsync(evt.EventId, cancellationToken))
        {
            return WebhookOutcome.Duplicate;
        }

        var now = clock.GetUtcNow();
        var at = evt.AtUtc > now + MaxFutureSkew ? now : evt.AtUtc;
        await presence.MarkProcessedAsync(new ProcessedMediaEvent(Guid.NewGuid(), tenantId, evt.EventId, evt.Type.ToString(), now), cancellationToken);

        switch (evt.Type)
        {
            case MediaEventType.ParticipantJoined:
                if (await presence.FindOpenAsync(meetingId, evt.Identity!, cancellationToken) is null)
                {
                    await presence.AddAsync(new PresenceRecord(Guid.NewGuid(), tenantId, meetingId, evt.Identity!, evt.DisplayName ?? string.Empty, at), cancellationToken);
                }

                break;
            case MediaEventType.ParticipantLeft:
                (await presence.FindOpenAsync(meetingId, evt.Identity!, cancellationToken))?.Leave(at);
                break;
            case MediaEventType.RoomFinished:
                foreach (var open in await presence.ListOpenAsync(meetingId, cancellationToken))
                {
                    open.Leave(at);
                }

                break;
        }

        await presence.SaveChangesAsync(cancellationToken);
        return WebhookOutcome.Recorded;
    }

    /// <summary>Stays and totals for one meeting; null if the meeting does not exist in the current tenant.</summary>
    public async Task<PresenceSummary?> SummaryAsync(Guid meetingId, CancellationToken cancellationToken)
    {
        if (await meetings.GetAsync(meetingId, cancellationToken) is not { } meeting)
        {
            return null;
        }

        // A stay that never got a "left" event is measured up to the end of the meeting (or now if the meeting is still running).
        var openUntil = meeting.EndedAtUtc ?? meeting.EndUtc ?? clock.GetUtcNow();
        var records = (await presence.ListAsync(meetingId, cancellationToken)).OrderBy(r => r.JoinedAtUtc).ToList();
        var stays = records.Select(r => new PresenceDto(r.Identity, r.DisplayName, r.JoinedAtUtc, r.LeftAtUtc, Math.Round(r.Duration(openUntil).TotalMinutes, 2))).ToList();
        return new PresenceSummary(records.Select(r => r.Identity).Distinct().Count(), Math.Round(stays.Sum(s => s.Minutes), 2), stays);
    }

    public Guid? CurrentTenant => tenant.Tenant?.Id;
}
