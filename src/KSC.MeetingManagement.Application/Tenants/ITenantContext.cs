using KSC.MeetingManagement.Domain.Tenants;

namespace KSC.MeetingManagement.Application.Tenants;

/// <summary>Tenant of the current request. Set once by the tenant resolution middleware.</summary>
public interface ITenantContext
{
    Tenant? Tenant { get; }

    Guid RequiredTenantId { get; }
}

public interface ITenantContextSetter
{
    void SetTenant(Tenant tenant);
}

public interface ITenantStore
{
    Task<Tenant?> FindByIdAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed class TenantNotResolvedException() : InvalidOperationException("No tenant has been resolved for this request.");
