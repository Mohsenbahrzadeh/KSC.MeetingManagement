using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Participants;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Media;

public enum MediaJoinOutcome
{
    Granted = 0,
    MeetingNotFound,
    NotAllowed,
    NotOnline,
    NotOpen,
    NotConfigured,

    /// <summary>The person asked to enter and is waiting for the host; ask again shortly.</summary>
    Waiting,
}

public sealed record MediaJoinResult(MediaJoinOutcome Outcome, MediaToken? Token = null)
{
    public bool Granted => Outcome == MediaJoinOutcome.Granted;
}

/// <summary>
/// Decides who may enter a meeting's room and with which capabilities, then asks the token service to sign exactly that.
/// A token is never issued unless every check passes; the room name is derived here, never taken from the caller.
/// </summary>
public sealed class MediaAccessService(
    IMeetingRepository meetings,
    IParticipantRepository participants,
    IRoomEntryRepository entries,
    IMediaTokenService tokens,
    MediaOptions options,
    ITenantContext tenant,
    ICurrentUser user,
    TimeProvider clock)
{
    /// <summary>Room name for a meeting. Includes the tenant so rooms of different organisations can never collide.</summary>
    public static string RoomName(Guid tenantId, Guid meetingId) => $"ksc-{tenantId:N}-{meetingId:N}";

    private static MediaJoinResult Counted(MediaJoinResult result)
    {
        Telemetry.KscTelemetry.RoomJoins.Add(1, new KeyValuePair<string, object?>("outcome", result.Outcome.ToString()));
        return result;
    }

    /// <summary>An authenticated organisation member joins: the organizer as host, an invited internal participant as participant.</summary>
    public async Task<MediaJoinResult> JoinAsync(Guid meetingId, string? displayName, CancellationToken cancellationToken) =>
        Counted(await JoinCoreAsync(meetingId, displayName, cancellationToken));

    private async Task<MediaJoinResult> JoinCoreAsync(Guid meetingId, string? displayName, CancellationToken cancellationToken)
    {
        if (await meetings.GetAsync(meetingId, cancellationToken) is not { } meeting)
        {
            return new MediaJoinResult(MediaJoinOutcome.MeetingNotFound);
        }

        var userId = user.RequiredUserId;
        MediaRole? role = null;
        if (meeting.OrganizerId == userId)
        {
            role = MediaRole.Host;
        }
        else
        {
            var invited = (await participants.ListAsync(meetingId, cancellationToken))
                .Any(p => p.UserId == userId && p.Status is InvitationStatus.Invited or InvitationStatus.Accepted);
            if (invited)
            {
                role = MediaRole.Participant;
            }
        }

        if (role is not { } granted)
        {
            return new MediaJoinResult(MediaJoinOutcome.NotAllowed);
        }

        var identity = $"u-{userId:N}";
        if (granted == MediaRole.Host)
        {
            // A host is never kept out by the lobby, but a removal is still respected.
            var hostEntry = await entries.FindAsync(meetingId, identity, cancellationToken);
            return hostEntry?.Status == RoomEntryStatus.Removed ? new MediaJoinResult(MediaJoinOutcome.NotAllowed) : Issue(meeting, identity, displayName, granted);
        }

        var gate = await GateAsync(meeting, identity, RoomEntryKind.Member, displayName, options.LobbyForMembers, cancellationToken);
        return gate == MediaJoinOutcome.Granted ? Issue(meeting, identity, displayName, granted) : new MediaJoinResult(gate);
    }

    /// <summary>
    /// A guest enters with the one-time invitation token. Any invalid, expired or revoked invitation gets the same refusal.
    /// </summary>
    public async Task<MediaJoinResult> JoinGuestAsync(Guid meetingId, Guid participantId, string? invitationToken, CancellationToken cancellationToken) =>
        Counted(await JoinGuestCoreAsync(meetingId, participantId, invitationToken, cancellationToken));

    private async Task<MediaJoinResult> JoinGuestCoreAsync(Guid meetingId, Guid participantId, string? invitationToken, CancellationToken cancellationToken)
    {
        if (await meetings.GetAsync(meetingId, cancellationToken) is not { } meeting)
        {
            return new MediaJoinResult(MediaJoinOutcome.NotAllowed);
        }

        // Check the room is open before consuming the invitation, so an early visit does not burn it.
        var precheck = Check(meeting);
        if (precheck != MediaJoinOutcome.Granted)
        {
            return precheck == MediaJoinOutcome.NotOpen && await participants.GetAsync(meetingId, participantId, cancellationToken) is null
                ? new MediaJoinResult(MediaJoinOutcome.NotAllowed)
                : new MediaJoinResult(precheck);
        }

        if (await participants.GetAsync(meetingId, participantId, cancellationToken) is not { } participant
            || !participant.TryRedeem(invitationToken, clock.GetUtcNow()))
        {
            return new MediaJoinResult(MediaJoinOutcome.NotAllowed);
        }

        await participants.SaveChangesAsync(cancellationToken);

        var guestIdentity = $"g-{participant.Id:N}";
        var guestGate = await GateAsync(meeting, guestIdentity, RoomEntryKind.Guest, participant.GuestName, options.LobbyForGuests, cancellationToken);
        return guestGate == MediaJoinOutcome.Granted ? Issue(meeting, guestIdentity, participant.GuestName, MediaRole.Guest) : new MediaJoinResult(guestGate);
    }

    /// <summary>
    /// Door check: removed or denied people are refused; with the lobby on, a newcomer is recorded as waiting until the host admits them;
    /// with it off, a newcomer is let in (and recorded) without waiting.
    /// </summary>
    private async Task<MediaJoinOutcome> GateAsync(
        Meeting meeting, string identity, RoomEntryKind kind, string? displayName, bool lobby, CancellationToken cancellationToken)
    {
        var entry = await entries.FindAsync(meeting.Id, identity, cancellationToken);
        if (entry is null)
        {
            if (!lobby)
            {
                return MediaJoinOutcome.Granted;
            }

            await entries.AddAsync(
                new RoomEntry(Guid.NewGuid(), tenant.RequiredTenantId, meeting.Id, identity, kind, displayName ?? string.Empty, clock.GetUtcNow()),
                cancellationToken);
            await entries.SaveChangesAsync(cancellationToken);
            return MediaJoinOutcome.Waiting;
        }

        return entry.Status switch
        {
            RoomEntryStatus.Admitted => MediaJoinOutcome.Granted,
            RoomEntryStatus.Waiting => lobby ? MediaJoinOutcome.Waiting : MediaJoinOutcome.Granted,
            _ => MediaJoinOutcome.NotAllowed,
        };
    }

    private MediaJoinResult Issue(Meeting meeting, string identity, string? displayName, MediaRole role)
    {
        var check = Check(meeting);
        if (check != MediaJoinOutcome.Granted)
        {
            return new MediaJoinResult(check);
        }

        var token = tokens.Issue(new MediaTokenRequest(
            RoomName(tenant.RequiredTenantId, meeting.Id),
            identity,
            displayName ?? string.Empty,
            role,
            TimeSpan.FromMinutes(Math.Clamp(options.TokenLifetimeMinutes, 1, MediaOptions.MaxLifetimeMinutes))));
        return new MediaJoinResult(MediaJoinOutcome.Granted, token);
    }

    private MediaJoinOutcome Check(Meeting meeting)
    {
        if (!tokens.IsConfigured)
        {
            return MediaJoinOutcome.NotConfigured;
        }

        if (meeting.Mode == MeetingMode.InPerson)
        {
            return MediaJoinOutcome.NotOnline;
        }

        if (meeting.Status is not (MeetingStatus.Scheduled or MeetingStatus.InProgress)
            || meeting.StartUtc is not { } start || meeting.EndUtc is not { } end)
        {
            return MediaJoinOutcome.NotOpen;
        }

        var now = clock.GetUtcNow();
        var opens = start - TimeSpan.FromMinutes(options.JoinOpensMinutesBeforeStart);
        var closes = end + TimeSpan.FromMinutes(options.JoinClosesMinutesAfterEnd);
        return now >= opens && now <= closes ? MediaJoinOutcome.Granted : MediaJoinOutcome.NotOpen;
    }
}
