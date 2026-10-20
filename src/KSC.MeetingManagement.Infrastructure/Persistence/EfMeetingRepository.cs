using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

public sealed class EfMeetingRepository(KscDbContext db) : IMeetingRepository
{
    // Serialises numbering inside one process to avoid needless retries; the concurrency token and the
    // unique index still protect against several API instances.
    private static readonly SemaphoreSlim NumberingGate = new(1, 1);

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

    public async Task AddNumberedAsync(Meeting meeting, int year, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meeting);

        // The counter row is a concurrency token: two concurrent requests cannot both advance it from the
        // same value, so the loser retries with the fresh value. The unique (tenant, number) index is the backstop.
        const int maxAttempts = 30;
        await NumberingGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var counter = await db.NumberCounters.FirstOrDefaultAsync(c => c.TenantId == meeting.TenantId && c.Year == year, cancellationToken);
                if (counter is null)
                {
                    counter = new MeetingNumberCounter(meeting.TenantId, year);
                    db.NumberCounters.Add(counter);
                }

                var next = counter.Advance();
                if (meeting.Number is null)
                {
                    meeting.AssignNumber($"{year}-{next:D4}");
                }

                db.Meetings.Add(meeting);
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }
                catch (Exception ex) when (ex is DbUpdateException && attempt < maxAttempts)
                {
                    // DbUpdateConcurrencyException derives from DbUpdateException.
                    foreach (var entry in db.ChangeTracker.Entries().ToList())
                    {
                        entry.State = EntityState.Detached;
                    }

                    meeting.ClearNumber();
                    await Task.Delay(Random.Shared.Next(1, 5 * attempt), cancellationToken);
                }
            }
        }
        finally
        {
            NumberingGate.Release();
        }
    }

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
