using KSC.MeetingManagement.Application.Media;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

public sealed class EfPresenceRepository(KscDbContext db) : IPresenceRepository
{
    public Task<bool> HasProcessedAsync(string eventId, CancellationToken cancellationToken) =>
        db.ProcessedMediaEvents.AnyAsync(p => p.EventId == eventId, cancellationToken);

    public async Task MarkProcessedAsync(ProcessedMediaEvent processed, CancellationToken cancellationToken) =>
        await db.ProcessedMediaEvents.AddAsync(processed, cancellationToken);

    public Task<PresenceRecord?> FindOpenAsync(Guid meetingId, string identity, CancellationToken cancellationToken) =>
        db.PresenceRecords.Where(p => p.MeetingId == meetingId && p.Identity == identity && p.LeftAtUtc == null)
            .OrderByDescending(p => p.JoinedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<PresenceRecord>> ListOpenAsync(Guid meetingId, CancellationToken cancellationToken) =>
        await db.PresenceRecords.Where(p => p.MeetingId == meetingId && p.LeftAtUtc == null).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PresenceRecord>> ListAsync(Guid meetingId, CancellationToken cancellationToken) =>
        await db.PresenceRecords.AsNoTracking().Where(p => p.MeetingId == meetingId).OrderBy(p => p.JoinedAtUtc).ToListAsync(cancellationToken);

    public async Task AddAsync(PresenceRecord record, CancellationToken cancellationToken) =>
        await db.PresenceRecords.AddAsync(record, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
