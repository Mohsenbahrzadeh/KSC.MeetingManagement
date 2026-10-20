using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Tenants;

namespace KSC.MeetingManagement.Infrastructure.Tenants;

/// <summary>Scoped per request.</summary>
public sealed class TenantContext : ITenantContext, ITenantContextSetter
{
    public Tenant? Tenant { get; private set; }

    public Guid RequiredTenantId => Tenant?.Id ?? throw new TenantNotResolvedException();

    public void SetTenant(Tenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (Tenant is not null)
        {
            throw new InvalidOperationException("Tenant already set for this request.");
        }

        Tenant = tenant;
    }
}
