using System.Collections.Concurrent;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Tenants;

namespace KSC.MeetingManagement.Infrastructure.Tenants;

/// <summary>Temporary store until the database is chosen (ADR-0002). Replaced by an EF Core store.</summary>
public sealed class InMemoryTenantStore : ITenantStore
{
    private readonly ConcurrentDictionary<Guid, Tenant> _tenants = new();

    public void Add(Tenant tenant) => _tenants[tenant.Id] = tenant;

    public Task<Tenant?> FindByIdAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(_tenants.GetValueOrDefault(tenantId));
}
