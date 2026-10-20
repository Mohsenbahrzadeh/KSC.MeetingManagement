using System.Collections.Concurrent;
using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Domain.Identity;

namespace KSC.MeetingManagement.Infrastructure.Tenants;

/// <summary>In-memory store used by tests and local experiments; production reads memberships from the database.</summary>
public sealed class InMemoryMembershipStore : IMembershipStore
{
    private readonly ConcurrentDictionary<(Guid Tenant, Guid User), Membership> _items = new();

    public void Add(Membership membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        _items[(membership.TenantId, membership.UserId)] = membership;
    }

    public Task<Membership?> FindAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(_items.GetValueOrDefault((tenantId, userId)));

    public Task<IReadOnlyList<Membership>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Membership>>(_items.Values.Where(m => m.UserId == userId).ToList());
}
