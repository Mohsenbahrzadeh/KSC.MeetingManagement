namespace KSC.MeetingManagement.Infrastructure.Persistence;

/// <summary>Durable, tenant-scoped invalidation record for the derived Lucene index.</summary>
public sealed class SearchIndexChange
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid MeetingId { get; set; }
}
