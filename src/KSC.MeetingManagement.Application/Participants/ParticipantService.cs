using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Participants;

public sealed class ParticipantService(
    IParticipantRepository participants,
    IMeetingRepository meetings,
    IMembershipStore memberships,
    ITenantContext tenant,
    TimeProvider clock)
{
    public static readonly TimeSpan MaxInviteLifetime = TimeSpan.FromDays(30);

    /// <summary>Adds a tenant member to the meeting. Returns null if the meeting does not exist.</summary>
    public async Task<ParticipantDto?> AddInternalAsync(Guid meetingId, Guid userId, bool allowConflicts, CancellationToken cancellationToken)
    {
        if (await meetings.GetAsync(meetingId, cancellationToken) is not { } meeting)
        {
            return null;
        }

        if (await memberships.FindAsync(tenant.RequiredTenantId, userId, cancellationToken) is not { IsActive: true })
        {
            throw new MeetingDomainException("The user is not an active member of this organisation.");
        }

        if (await participants.HasInternalAsync(meetingId, userId, cancellationToken))
        {
            throw new MeetingDomainException("The user is already a participant.");
        }

        if (!allowConflicts && meeting is { StartUtc: { } start, EndUtc: { } end, Status: MeetingStatus.Scheduled or MeetingStatus.InProgress })
        {
            var conflicts = await meetings.FindConflictsAsync([userId], start, end, meetingId, cancellationToken);
            if (conflicts.Count > 0)
            {
                throw new MeetingTimeConflictException(conflicts);
            }
        }

        var participant = MeetingParticipant.Internal(Guid.NewGuid(), tenant.RequiredTenantId, meetingId, userId);
        await participants.AddAsync(participant, cancellationToken);
        await participants.SaveChangesAsync(cancellationToken);
        return ParticipantDto.From(participant);
    }

    public async Task<GuestInvitationDto?> InviteGuestAsync(
        Guid meetingId, string name, string email, DateTimeOffset expiresUtc, CancellationToken cancellationToken)
    {
        if (!await MeetingExistsAsync(meetingId, cancellationToken))
        {
            return null;
        }

        var now = clock.GetUtcNow();
        if (expiresUtc > now + MaxInviteLifetime)
        {
            throw new MeetingDomainException($"Invitation lifetime cannot exceed {MaxInviteLifetime.TotalDays:0} days.");
        }

        var (participant, token) = MeetingParticipant.Guest(Guid.NewGuid(), tenant.RequiredTenantId, meetingId, name, email, expiresUtc, now);
        await participants.AddAsync(participant, cancellationToken);
        await participants.SaveChangesAsync(cancellationToken);
        return new GuestInvitationDto(ParticipantDto.From(participant), token);
    }

    public async Task<IReadOnlyList<ParticipantDto>> ListAsync(Guid meetingId, CancellationToken cancellationToken) =>
        (await participants.ListAsync(meetingId, cancellationToken)).Select(ParticipantDto.From).ToList();

    public async Task<ParticipantDto?> RevokeAsync(Guid meetingId, Guid participantId, CancellationToken cancellationToken)
    {
        if (await participants.GetAsync(meetingId, participantId, cancellationToken) is not { } participant)
        {
            return null;
        }

        participant.Revoke();
        await participants.SaveChangesAsync(cancellationToken);
        return ParticipantDto.From(participant);
    }

    /// <summary>Internal participant accepts or declines their own invitation.</summary>
    public async Task<ParticipantDto?> RespondAsync(
        Guid meetingId, Guid participantId, Guid callerUserId, bool accept, CancellationToken cancellationToken)
    {
        if (await participants.GetAsync(meetingId, participantId, cancellationToken) is not { } participant
            || participant.UserId != callerUserId)
        {
            return null;
        }

        participant.Respond(accept);
        await participants.SaveChangesAsync(cancellationToken);
        return ParticipantDto.From(participant);
    }

    /// <summary>
    /// Validates a guest token. Returns null for any invalid, expired, revoked or unknown invitation
    /// without revealing which, so an invalid invitation can never create access.
    /// </summary>
    public async Task<ParticipantDto?> RedeemAsync(Guid meetingId, Guid participantId, string? token, CancellationToken cancellationToken)
    {
        if (await participants.GetAsync(meetingId, participantId, cancellationToken) is not { } participant
            || !participant.TryRedeem(token, clock.GetUtcNow()))
        {
            return null;
        }

        await participants.SaveChangesAsync(cancellationToken);
        return ParticipantDto.From(participant);
    }

    private async Task<bool> MeetingExistsAsync(Guid meetingId, CancellationToken cancellationToken) =>
        await meetings.GetAsync(meetingId, cancellationToken) is not null;
}
