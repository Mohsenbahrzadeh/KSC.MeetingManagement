using KSC.MeetingManagement.Application.Audit;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

/// <summary>
/// Writes audit events with a context of its own, so a request whose own changes failed (and left tracked entities in a bad
/// state) can still record what happened. A failure to audit is logged and never breaks the request.
/// </summary>
public sealed partial class EfAuditLog(
    DbContextOptions<KscDbContext> options,
    ITenantContext tenant,
    IRequestInfo request,
    TimeProvider clock,
    ILogger<EfAuditLog> logger) : IAuditLog, IAuditQuery
{
    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (tenant.Tenant is null)
        {
            return;
        }

        try
        {
            var auditEvent = new AuditEvent(
                Guid.NewGuid(), tenant.Tenant.Id, clock.GetUtcNow(), entry.ActorType, entry.ActorId, entry.Action,
                entry.ResourceType, entry.ResourceId, entry.Outcome, request.CorrelationId, entry.Detail);

            await using var db = new KscDbContext(options, tenant);
            db.AuditEvents.Add(auditEvent);

            // Not tied to the request's cancellation: an aborted request must still leave its audit trail.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or ArgumentException or InvalidOperationException)
        {
            LogAuditFailure(ex, entry.Action);
        }
    }

    public async Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var db = new KscDbContext(options, tenant);
        IQueryable<AuditEvent> q = db.AuditEvents.AsNoTracking();

        if (query.From is { } from)
        {
            q = q.Where(e => e.OccurredAtUtc >= from);
        }

        if (query.To is { } to)
        {
            q = q.Where(e => e.OccurredAtUtc < to);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            var action = query.Action;
            q = q.Where(e => e.Action == action);
        }

        if (query.ActorId is { } actor)
        {
            q = q.Where(e => e.ActorId == actor);
        }

        if (query.ResourceId is { } resource)
        {
            q = q.Where(e => e.ResourceId == resource);
        }

        if (query.Outcome is { } outcome)
        {
            q = q.Where(e => e.Outcome == outcome);
        }

        return await q.OrderByDescending(e => e.OccurredAtUtc).ThenBy(e => e.Id).Skip(query.Skip).Take(query.Take).ToListAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to write audit event {Action}")]
    private partial void LogAuditFailure(Exception exception, string action);
}
