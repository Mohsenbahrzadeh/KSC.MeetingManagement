using KSC.MeetingManagement.Application.Reports;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

/// <summary>Read-only aggregates over the current tenant (the context's query filters apply).</summary>
public sealed class EfReportRepository(KscDbContext db) : IReportRepository
{
    public async Task<MeetingReport> MeetingsAsync(MeetingReportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var from = query.From;
        var to = query.To;
        IQueryable<Meeting> q = db.Meetings.AsNoTracking().Where(m => m.StartUtc != null && m.StartUtc >= from && m.StartUtc < to);

        if (query.Status is { } status)
        {
            q = q.Where(m => m.Status == status);
        }

        if (query.OrganizerId is { } organizer)
        {
            q = q.Where(m => m.OrganizerId == organizer);
        }

        if (query.CommitteeId is { } committee)
        {
            q = q.Where(m => m.CommitteeId == committee);
        }

        var counts = await q.GroupBy(m => m.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var rows = await q.OrderBy(m => m.StartUtc).ThenBy(m => m.Id).Skip(query.Skip).Take(query.Take)
            .Select(m => new MeetingReportRow(m.Id, m.Number, m.Title, m.Status, m.Mode, m.StartUtc, m.EndUtc, m.OrganizerId, m.CommitteeId))
            .ToListAsync(cancellationToken);

        return new MeetingReport(counts.Sum(c => c.Count), counts.ToDictionary(c => c.Status, c => c.Count), rows);
    }

    public async Task<IReadOnlyList<AttendanceRow>> AttendanceAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, int skip, int take, CancellationToken cancellationToken)
    {
        var meetings = await db.Meetings.AsNoTracking()
            .Where(m => m.StartUtc != null && m.StartUtc >= fromUtc && m.StartUtc < toUtc)
            .OrderBy(m => m.StartUtc).ThenBy(m => m.Id).Skip(skip).Take(take)
            .Select(m => new { m.Id, m.Number, m.Title, m.StartUtc, m.EndUtc, m.EndedAtUtc })
            .ToListAsync(cancellationToken);

        var ids = meetings.Select(m => m.Id).ToList();
        var participants = await db.Participants.AsNoTracking()
            .Where(p => ids.Contains(p.MeetingId))
            .Select(p => new { p.MeetingId, p.Status, p.Kind })
            .ToListAsync(cancellationToken);

        var stays = await db.PresenceRecords.AsNoTracking()
            .Where(p => ids.Contains(p.MeetingId))
            .Select(p => new { p.MeetingId, p.Identity, p.JoinedAtUtc, p.LeftAtUtc })
            .ToListAsync(cancellationToken);

        return meetings.Select(m =>
        {
            var mine = participants.Where(p => p.MeetingId == m.Id).ToList();
            var present = stays.Where(s => s.MeetingId == m.Id).ToList();

            // A stay without a "left" event is measured up to the end of the meeting.
            var openUntil = m.EndedAtUtc ?? m.EndUtc ?? DateTimeOffset.UtcNow;
            var minutes = present.Sum(s => Math.Max(0, ((s.LeftAtUtc ?? openUntil) - s.JoinedAtUtc).TotalMinutes));
            return new AttendanceRow(
                m.Id, m.Number, m.Title, m.StartUtc,
                mine.Count(p => p.Status == InvitationStatus.Invited),
                mine.Count(p => p.Status == InvitationStatus.Accepted),
                mine.Count(p => p.Status == InvitationStatus.Declined),
                mine.Count(p => p.Status == InvitationStatus.Revoked),
                mine.Count(p => p.Kind == ParticipantKind.Guest && p.Status != InvitationStatus.Revoked),
                present.Select(s => s.Identity).Distinct().Count(),
                Math.Round(minutes, 2));
        }).ToList();
    }

    public async Task<ActionsSummary> ActionsAsync(DateOnly today, DateOnly? dueFrom, DateOnly? dueTo, CancellationToken cancellationToken)
    {
        IQueryable<ActionItem> q = db.ActionItems.AsNoTracking();
        if (dueFrom is { } from)
        {
            q = q.Where(a => a.DueDate >= from);
        }

        if (dueTo is { } to)
        {
            q = q.Where(a => a.DueDate <= to);
        }

        var items = await q.Select(a => new { a.OwnerUserId, a.Status, a.DueDate }).ToListAsync(cancellationToken);
        bool Active(ActionItemStatus s) => s is ActionItemStatus.Open or ActionItemStatus.InProgress;
        bool Overdue(ActionItemStatus s, DateOnly due) => Active(s) && due < today;

        var byOwner = items
            .GroupBy(a => a.OwnerUserId)
            .Select(g => new OwnerActionSummary(
                g.Key,
                g.Count(a => Active(a.Status)),
                g.Count(a => Overdue(a.Status, a.DueDate)),
                g.Count(a => a.Status == ActionItemStatus.Done)))
            .OrderByDescending(o => o.Overdue).ThenByDescending(o => o.Active).ThenBy(o => o.OwnerUserId)
            .ToList();

        return new ActionsSummary(
            items.Count,
            items.Count(a => a.Status == ActionItemStatus.Open),
            items.Count(a => a.Status == ActionItemStatus.InProgress),
            items.Count(a => a.Status == ActionItemStatus.Done),
            items.Count(a => a.Status == ActionItemStatus.Cancelled),
            items.Count(a => Overdue(a.Status, a.DueDate)),
            byOwner);
    }

    public async Task<ResolutionsSummary> ResolutionsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        var meetingIds = db.Meetings.AsNoTracking().Where(m => m.StartUtc != null && m.StartUtc >= fromUtc && m.StartUtc < toUtc).Select(m => m.Id);
        var counts = await db.Resolutions.AsNoTracking()
            .Where(r => meetingIds.Contains(r.MeetingId))
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int Of(ResolutionStatus s) => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0;
        return new ResolutionsSummary(counts.Sum(c => c.Count), Of(ResolutionStatus.Draft), Of(ResolutionStatus.Adopted), Of(ResolutionStatus.Rejected), Of(ResolutionStatus.Revoked));
    }
}
