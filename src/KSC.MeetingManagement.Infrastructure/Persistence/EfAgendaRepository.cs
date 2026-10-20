using KSC.MeetingManagement.Application.Agenda;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

public sealed class EfAgendaRepository(KscDbContext db) : IAgendaRepository
{
    public async Task<IReadOnlyList<AgendaItem>> ListAsync(Guid meetingId, CancellationToken cancellationToken) =>
        await db.AgendaItems.Where(i => i.MeetingId == meetingId).OrderBy(i => i.Order).ToListAsync(cancellationToken);

    public async Task AddAsync(AgendaItem item, CancellationToken cancellationToken) =>
        await db.AgendaItems.AddAsync(item, cancellationToken);

    public void Remove(AgendaItem item) => db.AgendaItems.Remove(item);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new MeetingConcurrencyException("The agenda was changed by someone else; reload and retry.", ex);
        }
    }
}
