using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Notifications;

namespace KSC.MeetingManagement.Application.Notifications;

public sealed record OutboxStats(int Pending, int Sent, int Dead, int Skipped, double? OldestPendingAgeSeconds);

/// <summary>What an operator needs to judge a dead message. The text and the recipient address are deliberately left out.</summary>
public sealed record DeadMessageDto(Guid Id, NotificationKind Kind, RecipientType RecipientType, string Subject, int Attempts, string? LastError, DateTimeOffset CreatedAtUtc)
{
    public static DeadMessageDto From(OutboxMessage m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new(m.Id, m.Kind, m.RecipientType, m.Subject, m.Attempts, m.LastError, m.CreatedAtUtc);
    }
}

/// <summary>Operator view of the outbox. Tenant-scoped calls only see the current tenant; the global call is for metrics.</summary>
public interface IOutboxAdminStore
{
    Task<OutboxStats> StatsAsync(Guid? tenantId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task<IReadOnlyList<OutboxMessage>> DeadAsync(Guid tenantId, Guid? id, int skip, int take, bool track, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class OutboxAdminService(IOutboxAdminStore store, ITenantContext tenant, TimeProvider clock)
{
    public const int MaxPage = 200;

    public Task<OutboxStats> StatsAsync(CancellationToken cancellationToken) =>
        store.StatsAsync(tenant.RequiredTenantId, clock.GetUtcNow(), cancellationToken);

    public async Task<IReadOnlyList<DeadMessageDto>> ListDeadAsync(int skip, int take, CancellationToken cancellationToken) =>
        (await store.DeadAsync(tenant.RequiredTenantId, null, Math.Max(0, skip), Math.Clamp(take, 1, MaxPage), track: false, cancellationToken))
            .Select(DeadMessageDto.From).ToList();

    /// <summary>Puts one dead message back in the queue. Returns false when it does not exist in this tenant or is not dead.</summary>
    public async Task<bool> RequeueAsync(Guid id, CancellationToken cancellationToken)
    {
        var found = await store.DeadAsync(tenant.RequiredTenantId, id, 0, 1, track: true, cancellationToken);
        if (found.Count == 0)
        {
            return false;
        }

        found[0].Requeue(clock.GetUtcNow());
        await store.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Requeues up to <see cref="MaxPage"/> dead messages of this tenant; returns how many.</summary>
    public async Task<int> RequeueAllAsync(CancellationToken cancellationToken)
    {
        var dead = await store.DeadAsync(tenant.RequiredTenantId, null, 0, MaxPage, track: true, cancellationToken);
        var now = clock.GetUtcNow();
        foreach (var message in dead)
        {
            message.Requeue(now);
        }

        if (dead.Count > 0)
        {
            await store.SaveChangesAsync(cancellationToken);
        }

        return dead.Count;
    }
}
