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
    IMediaTokenService tokens,
    MediaOptions options,
    ITenantContext tenant,
    ICurrentUser user,
    TimeProvider clock)
{
    /// <summary>Room name for a meeting. Includes the tenant so rooms of different organisations can never collide.</summary>
    public static string RoomName(Guid tenantId, Guid meetingId) => $"ksc-{tenantId:N}-{meetingId:N}";

    /// <summary>An authenticated organisation member joins: the organizer as host, an invited internal participant as participant.</summary>
    public async Task<MediaJoinResult> JoinAsync(Guid meetingId, string? displayName, CancellationToken cancellationToken)
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

        return Issue(meeting, $"u-{userId:N}", displayName, granted);
    }

    /// <summary>
    /// A guest enters with the one-time invitation token. Any invalid, expired or revoked invitation gets the same refusal.
    /// </summary>
    public async Task<MediaJoinResult> JoinGuestAsync(Guid meetingId, Guid participantId, string? invitationToken, CancellationToken cancellationToken)
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
        return Issue(meeting, $"g-{participant.Id:N}", participant.GuestName, MediaRole.Guest);
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
