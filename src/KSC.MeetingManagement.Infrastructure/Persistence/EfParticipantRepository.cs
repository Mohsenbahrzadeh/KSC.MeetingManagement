using KSC.MeetingManagement.Application.Participants;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

public sealed class EfParticipantRepository(KscDbContext db) : IParticipantRepository
{
    public Task<MeetingParticipant?> GetAsync(Guid meetingId, Guid participantId, CancellationToken cancellationToken) =>
        db.Participants.FirstOrDefaultAsync(p => p.MeetingId == meetingId && p.Id == participantId, cancellationToken);

    public async Task<IReadOnlyList<MeetingParticipant>> ListAsync(Guid meetingId, CancellationToken cancellationToken) =>
        await db.Participants.AsNoTracking()
            .Where(p => p.MeetingId == meetingId)
            .OrderBy(p => p.Id)
            .ToListAsync(cancellationToken);

    public Task<bool> HasInternalAsync(Guid meetingId, Guid userId, CancellationToken cancellationToken) =>
        db.Participants.AnyAsync(
            p => p.MeetingId == meetingId && p.UserId == userId && p.Status != InvitationStatus.Revoked,
            cancellationToken);

    public async Task AddAsync(MeetingParticipant participant, CancellationToken cancellationToken) =>
        await db.Participants.AddAsync(participant, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
