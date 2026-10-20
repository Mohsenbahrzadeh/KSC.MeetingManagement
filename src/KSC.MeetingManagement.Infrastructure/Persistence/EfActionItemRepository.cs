using KSC.MeetingManagement.Application.Actions;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

public sealed class EfActionItemRepository(KscDbContext db) : IActionItemRepository
{
    public Task<ActionItem?> GetAsync(Guid meetingId, Guid actionId, CancellationToken cancellationToken) =>
        db.ActionItems.FirstOrDefaultAsync(a => a.MeetingId == meetingId && a.Id == actionId, cancellationToken);

    public async Task<IReadOnlyList<ActionItem>> ListForMeetingAsync(Guid meetingId, CancellationToken cancellationToken) =>
        await db.ActionItems.AsNoTracking().Where(a => a.MeetingId == meetingId).OrderBy(a => a.DueDate).ThenBy(a => a.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ActionItem>> QueryAsync(ActionItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        IQueryable<ActionItem> q = db.ActionItems.AsNoTracking();
        if (query.OwnerUserId is { } owner)
        {
            q = q.Where(a => a.OwnerUserId == owner);
        }

        if (query.Status is { } status)
        {
            q = q.Where(a => a.Status == status);
        }

        if (query.ActiveOnly)
        {
            q = q.Where(a => a.Status == ActionItemStatus.Open || a.Status == ActionItemStatus.InProgress);
        }

        if (query.DueBefore is { } before)
        {
            q = q.Where(a => a.DueDate < before);
        }

        return await q.OrderBy(a => a.DueDate).ThenBy(a => a.Id).Skip(query.Skip).Take(query.Take).ToListAsync(cancellationToken);
    }

    public async Task AddAsync(ActionItem item, CancellationToken cancellationToken) =>
        await db.ActionItems.AddAsync(item, cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new MeetingConcurrencyException("The action was changed by someone else; reload and retry.", ex);
        }
    }
}
