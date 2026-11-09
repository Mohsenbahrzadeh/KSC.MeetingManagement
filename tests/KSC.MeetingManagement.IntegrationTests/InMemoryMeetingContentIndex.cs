using KSC.MeetingManagement.Application.Meetings;

namespace KSC.MeetingManagement.IntegrationTests;

/// <summary>Search-index stub for API tests that do not exercise full-text behavior.</summary>
internal sealed class InMemoryMeetingContentIndex : IMeetingContentIndex
{
    private readonly Dictionary<Guid, long> _generations = [];
    private readonly HashSet<Guid> _readyTenants = [];

    public bool IsTenantReady(Guid tenantId) => _readyTenants.Contains(tenantId);

    public long GetTenantGeneration(Guid tenantId) => _generations.GetValueOrDefault(tenantId);

    public void MarkTenantReady(Guid tenantId, long generation)
    {
        _generations[tenantId] = generation;
        _readyTenants.Add(tenantId);
    }

    public Task ReplaceMeetingsAsync(Guid tenantId, IReadOnlyCollection<Guid> meetingIds, IReadOnlyCollection<MeetingSearchDocument> documents, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<MeetingSearchResult> SearchAsync(Guid tenantId, IReadOnlySet<Guid>? visibleMeetingIds, string text, int skip, int take, CancellationToken cancellationToken) =>
        Task.FromResult(new MeetingSearchResult(0, []));

    public Task RebuildTenantAsync(Guid tenantId, IReadOnlyCollection<MeetingSearchDocument> documents, CancellationToken cancellationToken)
    {
        _generations[tenantId] = GetTenantGeneration(tenantId) + 1;
        return Task.CompletedTask;
    }
}
