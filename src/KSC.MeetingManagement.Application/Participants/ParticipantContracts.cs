using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Participants;

public sealed record ParticipantDto(
    Guid Id,
    Guid MeetingId,
    ParticipantKind Kind,
    Guid? UserId,
    string? GuestName,
    string? GuestEmail,
    InvitationStatus Status,
    DateTimeOffset? InviteExpiresUtc)
{
    public static ParticipantDto From(MeetingParticipant p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.Id, p.MeetingId, p.Kind, p.UserId, p.GuestName, p.GuestEmail, p.Status, p.InviteExpiresUtc);
    }
}

/// <summary>Result of inviting a guest; Token is the only time the raw token is exposed.</summary>
public sealed record GuestInvitationDto(ParticipantDto Participant, string Token);

/// <summary>Persistence port. Implementations must only return participants of the current tenant.</summary>
public interface IParticipantRepository
{
    Task<MeetingParticipant?> GetAsync(Guid meetingId, Guid participantId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MeetingParticipant>> ListAsync(Guid meetingId, CancellationToken cancellationToken);

    Task<bool> HasInternalAsync(Guid meetingId, Guid userId, CancellationToken cancellationToken);

    Task AddAsync(MeetingParticipant participant, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
