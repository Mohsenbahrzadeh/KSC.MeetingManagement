using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Tenants;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

/// <summary>Builds an authorised, current-tenant corpus from the relational source of truth.</summary>
public sealed class EfMeetingSearchRepository(KscDbContext db, ITenantContext tenant) : IMeetingSearchRepository
{
    public async Task<IReadOnlySet<Guid>> GetVisibleMeetingIdsAsync(Guid userId, bool maySeeAll, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequiredTenantId;
        var meetings = db.Meetings.AsNoTracking().Where(meeting => meeting.TenantId == tenantId);
        if (!maySeeAll)
        {
            var participantMeetingIds = db.Participants.AsNoTracking()
                .Where(participant => participant.UserId == userId
                    && (participant.Status == Domain.Meetings.InvitationStatus.Invited || participant.Status == Domain.Meetings.InvitationStatus.Accepted))
                .Select(participant => participant.MeetingId);
            meetings = meetings.Where(meeting => meeting.OrganizerId == userId || participantMeetingIds.Contains(meeting.Id));
        }

        return (await meetings.Select(meeting => meeting.Id).ToListAsync(cancellationToken)).ToHashSet();
    }

    public async Task<IReadOnlyList<(Guid ChangeId, Guid MeetingId)>> GetPendingChangesAsync(int take, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequiredTenantId;
        var rows = await db.SearchIndexChanges.AsNoTracking()
            .Where(change => change.TenantId == tenantId)
            .OrderBy(change => change.Id)
            .Take(Math.Clamp(take, 1, 1000))
            .Select(change => new { change.Id, change.MeetingId })
            .ToListAsync(cancellationToken);
        return rows.Select(row => (row.Id, row.MeetingId)).ToList();
    }

    public async Task CompleteChangesAsync(IReadOnlyCollection<Guid> changeIds, CancellationToken cancellationToken)
    {
        if (changeIds.Count == 0) return;
        var tenantId = tenant.RequiredTenantId;
        var changes = await db.SearchIndexChanges
            .Where(change => change.TenantId == tenantId && changeIds.Contains(change.Id))
            .ToListAsync(cancellationToken);
        db.SearchIndexChanges.RemoveRange(changes);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MeetingSearchDocument>> SearchDocumentsAsync(
        string text, IReadOnlyCollection<Guid>? meetingIds, int take, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequiredTenantId;
        var limit = Math.Clamp(take, 1, KSC.MeetingManagement.Application.Meetings.MeetingSearchService.MaxIndexedDocuments + 1);
        var minutesQuery = db.Minutes.AsNoTracking()
            .Where(item => item.Status == Domain.Meetings.MinutesStatus.Approved);
        var attachmentsQuery = db.Attachments.AsNoTracking();
        if (meetingIds is not null)
        {
            var allowed = meetingIds.Distinct().ToArray();
            if (allowed.Length == 0) return [];
            minutesQuery = minutesQuery.Where(item => allowed.Contains(item.MeetingId));
            attachmentsQuery = attachmentsQuery.Where(item => allowed.Contains(item.MeetingId));
        }

        // Bound each source query, then let the application reject a corpus if the combined count exceeds its limit.
        var minutes = await minutesQuery.OrderBy(item => item.MeetingId)
            .Take(limit)
            .Select(item => new
            {
                item.TenantId,
                item.MeetingId,
                item.ApprovedVersionNumber,
                Content = item.Versions.Where(version => version.Number == item.ApprovedVersionNumber).Select(version => version.Content).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        var attachments = await attachmentsQuery.OrderBy(item => item.MeetingId).ThenBy(item => item.Id)
            .Take(limit)
            .Select(item => new
            {
                item.TenantId,
                item.MeetingId,
                item.Id,
                item.FileName,
                item.ContentType,
            })
            .ToListAsync(cancellationToken);

        var meetingIdsToLoad = minutes.Select(item => item.MeetingId)
            .Concat(attachments.Select(item => item.MeetingId))
            .Distinct()
            .ToArray();
        if (meetingIdsToLoad.Length == 0) return [];
        var meetingById = await db.Meetings.AsNoTracking()
            .Where(meeting => meetingIdsToLoad.Contains(meeting.Id) && meeting.TenantId == tenantId)
            .Select(meeting => new { meeting.Id, meeting.TenantId, meeting.Title, meeting.Number, meeting.StartUtc })
            .ToDictionaryAsync(meeting => meeting.Id, cancellationToken);

        // Full-text scope is intentionally limited to approved minutes and attachment metadata.
        var documents = new List<MeetingSearchDocument>(minutes.Count + attachments.Count);
        documents.AddRange(minutes.Where(item => item.Content is not null).Select(item =>
        {
            var meeting = meetingById[item.MeetingId];
            return new MeetingSearchDocument(item.TenantId, meeting.Id, meeting.Title, meeting.Number, item.Content, "minutes", meeting.Id,
                "صورت‌جلسهٔ مصوب", meeting.StartUtc ?? DateTimeOffset.MinValue);
        }));
        documents.AddRange(attachments.Select(item =>
        {
            var meeting = meetingById[item.MeetingId];
            return new MeetingSearchDocument(item.TenantId, meeting.Id, meeting.Title, meeting.Number,
                item.FileName + " " + item.ContentType, "attachment", item.Id, "پیوست", meeting.StartUtc ?? DateTimeOffset.MinValue);
        }));

        return documents;
    }
}
