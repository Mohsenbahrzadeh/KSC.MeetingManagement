namespace KSC.MeetingManagement.Domain.Meetings;

/// <summary>Per-tenant, per-year sequence used to build meeting numbers.</summary>
public sealed class MeetingNumberCounter
{
    public MeetingNumberCounter(Guid tenantId, int year)
    {
        TenantId = tenantId;
        Year = year;
    }

    public Guid TenantId { get; }
    public int Year { get; }
    public int LastValue { get; private set; }

    public int Advance() => ++LastValue;
}
