using System.Text.RegularExpressions;
using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Media;

public sealed record RoomEntryDto(string Identity, RoomEntryKind Kind, string DisplayName, RoomEntryStatus Status, DateTimeOffset RequestedAtUtc)
{
    public static RoomEntryDto From(RoomEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new(e.Identity, e.Kind, e.DisplayName, e.Status, e.RequestedAtUtc);
    }
}

/// <summary>Persistence port for who stands at the door of which room. Implementations must only return entries of the current tenant.</summary>
public interface IRoomEntryRepository
{
    Task<RoomEntry?> FindAsync(Guid meetingId, string identity, CancellationToken cancellationToken);

    Task<IReadOnlyList<RoomEntry>> ListAsync(Guid meetingId, RoomEntryStatus? status, CancellationToken cancellationToken);

    Task AddAsync(RoomEntry entry, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Acts on the live media room (end it, remove somebody). Best effort: the system stays correct without it because tokens are short-lived.</summary>
public interface IMediaRoomControl
{
    Task CloseRoomAsync(string room, CancellationToken cancellationToken);

    Task RemoveParticipantAsync(string room, string identity, CancellationToken cancellationToken);
}

/// <summary>Used when no media server is configured.</summary>
public sealed class NoMediaRoomControl : IMediaRoomControl
{
    public Task CloseRoomAsync(string room, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RemoveParticipantAsync(string room, string identity, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Host actions on a room: let people in or refuse them, remove them, and close the room when the meeting ends.</summary>
public sealed partial class RoomControlService(
    IRoomEntryRepository entries,
    IMeetingRepository meetings,
    IMediaRoomControl room,
    ITenantContext tenant,
    ICurrentUser user,
    TimeProvider clock)
{
    [GeneratedRegex("^[ug]-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPattern();

    public static bool IsValidIdentity(string? identity) => identity is not null && IdentityPattern().IsMatch(identity);

    public async Task<IReadOnlyList<RoomEntryDto>?> WaitingAsync(Guid meetingId, CancellationToken cancellationToken)
    {
        if (await meetings.GetAsync(meetingId, cancellationToken) is null)
        {
            return null;
        }

        return (await entries.ListAsync(meetingId, RoomEntryStatus.Waiting, cancellationToken)).OrderBy(e => e.RequestedAtUtc).Select(RoomEntryDto.From).ToList();
    }

    public Task<RoomEntryDto?> AdmitAsync(Guid meetingId, string identity, CancellationToken cancellationToken) =>
        DecideAsync(meetingId, identity, e => e.Admit(user.RequiredUserId, clock.GetUtcNow()), cancellationToken);

    public Task<RoomEntryDto?> DenyAsync(Guid meetingId, string identity, CancellationToken cancellationToken) =>
        DecideAsync(meetingId, identity, e => e.Deny(user.RequiredUserId, clock.GetUtcNow()), cancellationToken);

    /// <summary>
    /// Removes a person: they are marked removed (so no new token is issued to them for this meeting) and, if a media server is
    /// connected, disconnected right away. Works for people who never asked yet, so a host can bar someone in advance.
    /// </summary>
    public async Task<RoomEntryDto?> RemoveAsync(Guid meetingId, string identity, CancellationToken cancellationToken)
    {
        if (!IsValidIdentity(identity) || await meetings.GetAsync(meetingId, cancellationToken) is null)
        {
            return null;
        }

        var entry = await entries.FindAsync(meetingId, identity, cancellationToken);
        if (entry is null)
        {
            entry = new RoomEntry(Guid.NewGuid(), tenant.RequiredTenantId, meetingId, identity,
                identity.StartsWith('g') ? RoomEntryKind.Guest : RoomEntryKind.Member, string.Empty, clock.GetUtcNow());
            await entries.AddAsync(entry, cancellationToken);
        }

        entry.Remove(user.RequiredUserId, clock.GetUtcNow());
        await entries.SaveChangesAsync(cancellationToken);

        try
        {
            await room.RemoveParticipantAsync(MediaAccessService.RoomName(tenant.RequiredTenantId, meetingId), identity, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The person is already barred from new tokens; disconnecting is best effort.
            Telemetry.KscTelemetry.RoomControlFailures.Add(1, new KeyValuePair<string, object?>("action", "remove"));
        }

        return RoomEntryDto.From(entry);
    }

    /// <summary>Closes the live room after the meeting ended. Never throws: the meeting is already over in our records.</summary>
    public async Task CloseRoomAsync(Guid meetingId, CancellationToken cancellationToken)
    {
        try
        {
            await room.CloseRoomAsync(MediaAccessService.RoomName(tenant.RequiredTenantId, meetingId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort; a connected person simply loses access when their token expires and they cannot get a new one.
            Telemetry.KscTelemetry.RoomControlFailures.Add(1, new KeyValuePair<string, object?>("action", "close"));
        }
    }

    private async Task<RoomEntryDto?> DecideAsync(Guid meetingId, string identity, Action<RoomEntry> decide, CancellationToken cancellationToken)
    {
        if (!IsValidIdentity(identity) || await entries.FindAsync(meetingId, identity, cancellationToken) is not { } entry)
        {
            return null;
        }

        decide(entry);
        await entries.SaveChangesAsync(cancellationToken);
        return RoomEntryDto.From(entry);
    }
}
