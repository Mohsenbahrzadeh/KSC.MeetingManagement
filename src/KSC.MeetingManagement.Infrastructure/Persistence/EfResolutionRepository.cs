using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Resolutions;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

public sealed class EfResolutionRepository(KscDbContext db) : IResolutionRepository
{
    private const int MaxAttempts = 8;

    public Task<Resolution?> GetAsync(Guid meetingId, Guid resolutionId, CancellationToken cancellationToken) =>
        db.Resolutions.FirstOrDefaultAsync(r => r.MeetingId == meetingId && r.Id == resolutionId, cancellationToken);

    public async Task<IReadOnlyList<Resolution>> ListAsync(Guid meetingId, CancellationToken cancellationToken) =>
        await db.Resolutions.AsNoTracking().Where(r => r.MeetingId == meetingId).OrderBy(r => r.Number).ToListAsync(cancellationToken);

    public Task<bool> AnyForAgendaItemAsync(Guid agendaItemId, CancellationToken cancellationToken) =>
        db.Resolutions.AnyAsync(r => r.AgendaItemId == agendaItemId, cancellationToken);

    public async Task AddNumberedAsync(Resolution resolution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        // The unique (tenant, meeting, number) index rejects a duplicate number from a concurrent request; the loser retries with a fresh maximum.
        for (var attempt = 1; ; attempt++)
        {
            var last = await db.Resolutions
                .Where(r => r.MeetingId == resolution.MeetingId)
                .MaxAsync(r => (int?)r.Number, cancellationToken) ?? 0;
            resolution.AssignNumber(last + 1);

            db.Resolutions.Add(resolution);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                db.Entry(resolution).State = EntityState.Detached;
                await Task.Delay(Random.Shared.Next(1, 5 * attempt), cancellationToken);
            }
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
            throw new MeetingConcurrencyException("The resolution was changed by someone else; reload and retry.", ex);
        }
    }
}
