using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace KSC.MeetingManagement.UnitTests;

public sealed class LuceneSearchTests
{
    [Fact]
    public void Persian_normalization_maps_arabic_letters_digits_and_half_spaces()
    {
        Assert.Equal("می رود 123", PersianSearchText.Normalize("مي‌روَد ۱۲۳"));
        Assert.Equal("کیف", PersianSearchText.Normalize("كیف"));
    }

    [Fact]
    public async Task Search_requires_all_terms_pages_results_and_isolates_tenants()
    {
        var root = Path.Combine(Path.GetTempPath(), "ksc-lucene-tests", Guid.NewGuid().ToString("N"));
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var meetingA = Guid.NewGuid();
        var meetingB = Guid.NewGuid();
        using var index = new LuceneMeetingContentIndex(root, NullLogger<LuceneMeetingContentIndex>.Instance);
        await index.RebuildTenantAsync(tenantA,
        [
            Document(tenantA, meetingA, "بودجه سالانه تصویب شد"),
            Document(tenantA, Guid.NewGuid(), "بودجه سالانه بررسی شود"),
        ], CancellationToken.None);
        await index.RebuildTenantAsync(tenantB, [Document(tenantB, meetingB, "بودجه سالانه محرمانه")], CancellationToken.None);

        var first = await index.SearchAsync(tenantA, null, "بودجه سالانه", 0, 1, CancellationToken.None);
        var second = await index.SearchAsync(tenantA, null, "بودجه سالانه", 1, 1, CancellationToken.None);
        var authorizedPage = await index.SearchAsync(tenantA, new HashSet<Guid> { first.Hits[0].MeetingId }, "بودجه سالانه", 0, 20, CancellationToken.None);
        var noMatch = await index.SearchAsync(tenantA, null, "بودجه محرمانه", 0, 10, CancellationToken.None);
        var tenantBResult = await index.SearchAsync(tenantB, null, "بودجه محرمانه", 0, 10, CancellationToken.None);

        Assert.Equal(2, first.TotalCount);
        Assert.Single(first.Hits);
        Assert.Single(second.Hits);
        Assert.Equal(1, authorizedPage.TotalCount);
        Assert.Equal(first.Hits[0].MeetingId, Assert.Single(authorizedPage.Hits).MeetingId);
        Assert.Equal(0, noMatch.TotalCount);
        Assert.Equal(meetingB, Assert.Single(tenantBResult.Hits).MeetingId);
    }

    [Fact]
    public async Task Updating_and_removing_meeting_documents_does_not_leave_old_terms_or_cross_tenant_deletes()
    {
        var root = Path.Combine(Path.GetTempPath(), "ksc-lucene-tests", Guid.NewGuid().ToString("N"));
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var sameMeetingId = Guid.NewGuid();
        using var index = new LuceneMeetingContentIndex(root, NullLogger<LuceneMeetingContentIndex>.Instance);
        await index.RebuildTenantAsync(tenantA, [Document(tenantA, sameMeetingId, "قدیمی")], CancellationToken.None);
        await index.RebuildTenantAsync(tenantB, [Document(tenantB, sameMeetingId, "مستقل")], CancellationToken.None);

        await index.ReplaceMeetingsAsync(tenantA, [sameMeetingId], [Document(tenantA, sameMeetingId, "جدید")], CancellationToken.None);

        Assert.Equal(0, (await index.SearchAsync(tenantA, null, "قدیمی", 0, 10, CancellationToken.None)).TotalCount);
        Assert.Equal(sameMeetingId, Assert.Single((await index.SearchAsync(tenantA, null, "جدید", 0, 10, CancellationToken.None)).Hits).MeetingId);
        Assert.Equal(sameMeetingId, Assert.Single((await index.SearchAsync(tenantB, null, "مستقل", 0, 10, CancellationToken.None)).Hits).MeetingId);

        await index.ReplaceMeetingsAsync(tenantA, [sameMeetingId], [], CancellationToken.None);
        Assert.Equal(0, (await index.SearchAsync(tenantA, null, "جدید", 0, 10, CancellationToken.None)).TotalCount);
        Assert.Equal(1, (await index.SearchAsync(tenantB, null, "مستقل", 0, 10, CancellationToken.None)).TotalCount);
    }

    private static MeetingSearchDocument Document(Guid tenantId, Guid meetingId, string content) =>
        new(tenantId, meetingId, "جلسهٔ آزمایشی", "۱۴۰۵-۰۰۰۱", content, "minutes", Guid.NewGuid(), "صورت‌جلسه", DateTimeOffset.UtcNow);
}
