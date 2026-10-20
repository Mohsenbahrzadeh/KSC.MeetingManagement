using KSC.MeetingManagement.Application.Attachments;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

public sealed class EfAttachmentRepository(KscDbContext db) : IAttachmentRepository
{
    public Task<MeetingAttachment?> GetAsync(Guid meetingId, Guid attachmentId, CancellationToken cancellationToken) =>
        db.Attachments.FirstOrDefaultAsync(a => a.MeetingId == meetingId && a.Id == attachmentId, cancellationToken);

    public async Task<IReadOnlyList<MeetingAttachment>> ListAsync(Guid meetingId, CancellationToken cancellationToken) =>
        await db.Attachments.AsNoTracking()
            .Where(a => a.MeetingId == meetingId)
            .OrderBy(a => a.UploadedAtUtc)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(MeetingAttachment attachment, CancellationToken cancellationToken) =>
        await db.Attachments.AddAsync(attachment, cancellationToken);

    public void Remove(MeetingAttachment attachment) => db.Attachments.Remove(attachment);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
