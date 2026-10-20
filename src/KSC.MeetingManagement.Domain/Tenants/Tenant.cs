namespace KSC.MeetingManagement.Domain.Tenants;

public enum TenantStatus
{
    Active = 1,
    Suspended = 2,
}

/// <summary>Organization boundary. All business records belong to exactly one tenant.</summary>
public sealed class Tenant
{
    public Tenant(Guid id, string slug, string name, string timeZoneId)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        Id = id;
        Slug = slug.Trim().ToLowerInvariant();
        Name = name.Trim();
        TimeZoneId = timeZoneId;
        Status = TenantStatus.Active;
    }

    public Guid Id { get; }
    public string Slug { get; }
    public string Name { get; private set; }
    public string TimeZoneId { get; private set; }
    public TenantStatus Status { get; private set; }

    public bool IsActive => Status == TenantStatus.Active;

    public void Suspend() => Status = TenantStatus.Suspended;

    public void Activate() => Status = TenantStatus.Active;
}
