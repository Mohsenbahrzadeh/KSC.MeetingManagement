using KSC.MeetingManagement.Application.Committees;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

public sealed class EfCommitteeRepository(KscDbContext db) : ICommitteeRepository
{
    public Task<Committee?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Committees.Include(c => c.Members).FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Committee>> ListAsync(CancellationToken cancellationToken) =>
        await db.Committees.AsNoTracking().Include(c => c.Members).OrderBy(c => c.Name).ToListAsync(cancellationToken);

    public async Task AddAsync(Committee committee, CancellationToken cancellationToken) =>
        await db.Committees.AddAsync(committee, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
