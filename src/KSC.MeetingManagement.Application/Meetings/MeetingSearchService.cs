using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Tenants;

namespace KSC.MeetingManagement.Application.Meetings;

public sealed class MeetingSearchService(IMeetingSearchRepository repository, IMeetingContentIndex index, ITenantContext tenant, ICurrentUser currentUser)
{
    public const int MaxQueryLength = 200;
    public const int MaxPageSize = 100;
    public const int MaxIndexedDocuments = 100_000;

    public async Task<MeetingSearchResult> SearchAsync(MeetingSearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var text = query.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("عبارت جست‌وجو را وارد کنید.");
        if (text.Length > MaxQueryLength) throw new ArgumentException($"عبارت جست‌وجو نباید بیشتر از {MaxQueryLength} نویسه باشد.");
        if (text.Any(char.IsControl)) throw new ArgumentException("عبارت جست‌وجو شامل نویسهٔ نامعتبر است.");
        if (text.All(char.IsPunctuation)) throw new ArgumentException("عبارت جست‌وجو باید شامل دست‌کم یک واژه باشد.");

        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take, 1, MaxPageSize);
        if (skip > 10_000) throw new ArgumentException("شمارهٔ صفحه از محدودهٔ مجاز خارج است.");
        var tenantId = tenant.RequiredTenantId;
        if (!index.IsTenantReady(tenantId))
        {
            var pendingBeforeRebuild = await repository.GetPendingChangesAsync(1000, cancellationToken);
            var documents = await repository.SearchDocumentsAsync(string.Empty, null, MaxIndexedDocuments + 1, cancellationToken);
            EnsureCorpusLimit(documents);
            await index.RebuildTenantAsync(tenantId, documents, cancellationToken);
            index.MarkTenantReady(tenantId, index.GetTenantGeneration(tenantId));
            await repository.CompleteChangesAsync(pendingBeforeRebuild.Select(change => change.ChangeId).ToArray(), cancellationToken);
        }

        var durableChanges = await repository.GetPendingChangesAsync(500, cancellationToken);
        var changedMeetingIds = durableChanges.Select(change => change.MeetingId).Distinct().ToArray();
        if (changedMeetingIds.Length > 0)
        {
            var documents = await repository.SearchDocumentsAsync(string.Empty, changedMeetingIds, MaxIndexedDocuments + 1, cancellationToken);
            EnsureCorpusLimit(documents);
            await index.ReplaceMeetingsAsync(tenantId, changedMeetingIds, documents, cancellationToken);
            await repository.CompleteChangesAsync(durableChanges.Select(change => change.ChangeId).ToArray(), cancellationToken);
        }

        IReadOnlySet<Guid>? visibleIds = query.MaySeeAllMeetings
            ? null
            : await repository.GetVisibleMeetingIdsAsync(currentUser.RequiredUserId, maySeeAll: false, cancellationToken);
        return await index.SearchAsync(tenantId, visibleIds, text, skip, take, cancellationToken);
    }

    public async Task<int> RebuildAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequiredTenantId;
        var pendingBeforeRebuild = await repository.GetPendingChangesAsync(1000, cancellationToken);
        var documents = await repository.SearchDocumentsAsync(string.Empty, null, MaxIndexedDocuments + 1, cancellationToken);
        EnsureCorpusLimit(documents);
        await index.RebuildTenantAsync(tenantId, documents, cancellationToken);
        index.MarkTenantReady(tenantId, index.GetTenantGeneration(tenantId));
        await repository.CompleteChangesAsync(pendingBeforeRebuild.Select(change => change.ChangeId).ToArray(), cancellationToken);
        return documents.Count;
    }

    private static void EnsureCorpusLimit(IReadOnlyCollection<MeetingSearchDocument> documents)
    {
        if (documents.Count > MaxIndexedDocuments)
            throw new InvalidOperationException("Search corpus exceeds the configured indexing limit.");
    }
}

/// <summary>Durable full-text index operations. Tenant IDs are mandatory on all operations.</summary>
public interface IMeetingContentIndex
{
    bool IsTenantReady(Guid tenantId);
    long GetTenantGeneration(Guid tenantId);
    void MarkTenantReady(Guid tenantId, long generation);
    Task ReplaceMeetingsAsync(Guid tenantId, IReadOnlyCollection<Guid> meetingIds, IReadOnlyCollection<MeetingSearchDocument> documents, CancellationToken cancellationToken);
    Task<MeetingSearchResult> SearchAsync(Guid tenantId, IReadOnlySet<Guid>? visibleMeetingIds, string text, int skip, int take, CancellationToken cancellationToken);
    Task RebuildTenantAsync(Guid tenantId, IReadOnlyCollection<MeetingSearchDocument> documents, CancellationToken cancellationToken);
}
