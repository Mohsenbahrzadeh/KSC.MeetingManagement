using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

public sealed class EfMeetingRepository(KscDbContext db) : IMeetingRepository
{
    public Task<Meeting?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Meetings.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Meeting>> ListAsync(MeetingQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        IQueryable<Meeting> q = db.Meetings.AsNoTracking();

        if (query.Status is { } status)
        {
            q = q.Where(m => m.Status == status);
        }

        // Overlap test so meetings crossing a range border are still returned.
        if (query.From is { } from)
        {
            q = q.Where(m => m.EndUtc > from);
        }

        if (query.To is { } to)
        {
            q = q.Where(m => m.StartUtc < to);
        }

        return await q
            .OrderBy(m => m.StartUtc)
            .ThenBy(m => m.Id)
            .Skip(query.Skip)
            .Take(query.Take)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Meeting meeting, CancellationToken cancellationToken) =>
        await db.Meetings.AddAsync(meeting, cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new MeetingConcurrencyException("The meeting was changed by someone else; reload and retry.", ex);
        }
    }
}
