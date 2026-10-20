using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Minutes;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

public sealed class EfMinutesRepository(KscDbContext db) : IMinutesRepository
{
    public Task<Minutes?> GetByMeetingAsync(Guid meetingId, CancellationToken cancellationToken) =>
        db.Minutes
            .Include(m => m.Versions)
            .Include(m => m.Transitions)
            .FirstOrDefaultAsync(m => m.MeetingId == meetingId, cancellationToken);

    public async Task AddAsync(Minutes minutes, CancellationToken cancellationToken) =>
        await db.Minutes.AddAsync(minutes, cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new MeetingConcurrencyException("The minutes were changed by someone else; reload and retry.", ex);
        }
    }
}
