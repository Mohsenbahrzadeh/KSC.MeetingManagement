using KSC.MeetingManagement.Application.Chat;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

public sealed class EfChatRepository(KscDbContext db) : IChatRepository
{
    private const int MaxAttempts = 8;

    public async Task<IReadOnlyList<ChatMessage>> ListAsync(Guid meetingId, long afterSequence, int take, CancellationToken cancellationToken) =>
        await db.ChatMessages.AsNoTracking()
            .Where(m => m.MeetingId == meetingId && m.Sequence > afterSequence)
            .OrderBy(m => m.Sequence)
            .Take(take)
            .ToListAsync(cancellationToken);

    public Task<ChatMessage?> GetAsync(Guid meetingId, Guid messageId, CancellationToken cancellationToken) =>
        db.ChatMessages.FirstOrDefaultAsync(m => m.MeetingId == meetingId && m.Id == messageId, cancellationToken);

    public async Task AddNumberedAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // The unique (tenant, meeting, sequence) index rejects a duplicate number from a concurrent sender; the loser retries.
        for (var attempt = 1; ; attempt++)
        {
            var last = await db.ChatMessages.Where(m => m.MeetingId == message.MeetingId).MaxAsync(m => (long?)m.Sequence, cancellationToken) ?? 0;
            message.AssignSequence(last + 1);
            db.ChatMessages.Add(message);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                db.Entry(message).State = EntityState.Detached;
                await Task.Delay(Random.Shared.Next(1, 5 * attempt), cancellationToken);
            }
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
