using KSC.MeetingManagement.Application.Media;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

public sealed class EfRoomEntryRepository(KscDbContext db) : IRoomEntryRepository
{
    public Task<RoomEntry?> FindAsync(Guid meetingId, string identity, CancellationToken cancellationToken) =>
        db.RoomEntries.FirstOrDefaultAsync(e => e.MeetingId == meetingId && e.Identity == identity, cancellationToken);

    public async Task<IReadOnlyList<RoomEntry>> ListAsync(Guid meetingId, RoomEntryStatus? status, CancellationToken cancellationToken)
    {
        IQueryable<RoomEntry> q = db.RoomEntries.AsNoTracking().Where(e => e.MeetingId == meetingId);
        if (status is { } s)
        {
            q = q.Where(e => e.Status == s);
        }

        return await q.OrderBy(e => e.RequestedAtUtc).ThenBy(e => e.Id).ToListAsync(cancellationToken);
    }

    public async Task AddAsync(RoomEntry entry, CancellationToken cancellationToken) =>
        await db.RoomEntries.AddAsync(entry, cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new MeetingConcurrencyException("The entry was changed by someone else; reload and retry.", ex);
        }
    }
}
