using System.Collections.Concurrent;
using System.Globalization;
using KSC.MeetingManagement.Application.Meetings;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Queries;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Microsoft.Extensions.Logging;

namespace KSC.MeetingManagement.Infrastructure.Search;

/// <summary>
/// Durable Lucene.NET index. Every query has a mandatory tenant filter, and every replacement/deletion is tenant scoped.
/// The database remains authoritative; the index can always be rebuilt.
/// </summary>
public sealed class LuceneMeetingContentIndex : IMeetingContentIndex, IDisposable
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;
    private const string TenantField = "tenant_id";
    private const string ContentField = "content";
    private readonly object _gate = new();
    private readonly Analyzer _analyzer;
    private readonly Lucene.Net.Store.Directory _directory;
    private readonly IndexWriter _writer;
    private readonly ConcurrentDictionary<Guid, long> _tenantGenerations = new();
    private readonly ConcurrentDictionary<Guid, long> _readyGenerations = new();

    public LuceneMeetingContentIndex(string rootPath, ILogger<LuceneMeetingContentIndex> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(logger);
        System.IO.Directory.CreateDirectory(rootPath);
        _directory = FSDirectory.Open(new DirectoryInfo(rootPath));
        _analyzer = new PersianAnalyzer();
        _writer = new IndexWriter(_directory, new IndexWriterConfig(Version, _analyzer)
        {
            OpenMode = OpenMode.CREATE_OR_APPEND,
            RAMBufferSizeMB = 32,
        });
    }

    public bool IsTenantReady(Guid tenantId) =>
        _readyGenerations.TryGetValue(tenantId, out var ready) && ready == GetTenantGeneration(tenantId);

    public long GetTenantGeneration(Guid tenantId) => _tenantGenerations.GetOrAdd(tenantId, 0);

    public Task ReplaceMeetingsAsync(Guid tenantId, IReadOnlyCollection<Guid> meetingIds, IReadOnlyCollection<MeetingSearchDocument> documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meetingIds);
        ArgumentNullException.ThrowIfNull(documents);
        if (tenantId == Guid.Empty || documents.Any(document => document.TenantId != tenantId || !meetingIds.Contains(document.MeetingId)))
            throw new ArgumentException("Replacement documents must belong to the tenant and meeting ids specified.", nameof(documents));

        lock (_gate)
        {
            foreach (var id in meetingIds.Distinct())
            {
                var meetingQuery = new BooleanQuery
                {
                    { new TermQuery(new Term("meeting_id", id.ToString("D"))), Occur.MUST },
                    { new TermQuery(new Term(TenantField, tenantId.ToString("N"))), Occur.MUST },
                };
                _writer.DeleteDocuments(meetingQuery);
            }
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(document.Content)) _writer.AddDocument(ToLuceneDocument(document));
            }
            _writer.Commit();
        }
        return Task.CompletedTask;
    }

    public void MarkTenantReady(Guid tenantId, long generation)
    {
        _readyGenerations[tenantId] = generation;
    }

    public Task RebuildTenantAsync(Guid tenantId, IReadOnlyCollection<MeetingSearchDocument> documents, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Any(document => document.TenantId != tenantId))
            throw new ArgumentException("A tenant rebuild cannot contain another tenant's documents.", nameof(documents));
        MarkTenantDirty(tenantId);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writer.DeleteDocuments(new Term(TenantField, tenantId.ToString("N")));
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(document.Content)) _writer.AddDocument(ToLuceneDocument(document));
            }
            _writer.Commit();
        }
        return Task.CompletedTask;
    }

    private void MarkTenantDirty(Guid tenantId) =>
        _tenantGenerations.AddOrUpdate(tenantId, 1, static (_, current) => checked(current + 1));

    public Task<MeetingSearchResult> SearchAsync(Guid tenantId, IReadOnlySet<Guid>? visibleMeetingIds, string text, int skip, int take, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = PersianSearchText.Normalize(text);
        if (normalized.Length == 0) return Task.FromResult(new MeetingSearchResult(0, []));
        if (visibleMeetingIds is { Count: 0 }) return Task.FromResult(new MeetingSearchResult(0, []));

        lock (_gate)
        {
            using var reader = DirectoryReader.Open(_writer, applyAllDeletes: true);
            var searcher = new IndexSearcher(reader);
            var tenantQuery = new BooleanQuery { { new TermQuery(new Term(TenantField, tenantId.ToString("N"))), Occur.MUST } };
            foreach (var token in PersianSearchText.Tokens(normalized))
                tenantQuery.Add(new TermQuery(new Term(ContentField, token)), Occur.MUST);

            Filter? visibilityFilter = visibleMeetingIds is null
                ? null
                : new TermsFilter(visibleMeetingIds.Select(id => new Term("meeting_id", id.ToString("D"))).ToList());
            var tenantMatches = searcher.Search(tenantQuery, visibilityFilter, 100_001);
            var hits = tenantMatches.ScoreDocs.Skip(skip).Take(take).Select(scoreDoc =>
            {
                var document = searcher.Doc(scoreDoc.Doc);
                var content = Stored(document, "content_stored");
                return new MeetingSearchHit(
                    Guid.Parse(Stored(document, "meeting_id")), Stored(document, "meeting_title"), EmptyToNull(Stored(document, "meeting_number")),
                    Stored(document, "kind"), Guid.TryParse(Stored(document, "source_id"), out var sourceId) ? sourceId : null,
                    EmptyToNull(Stored(document, "source_label")), PersianSearchText.Snippet(content, normalized),
                    new DateTimeOffset(long.Parse(Stored(document, "updated_ticks"), CultureInfo.InvariantCulture), TimeSpan.Zero));
            }).ToList();
            return Task.FromResult(new MeetingSearchResult(tenantMatches.TotalHits, hits));
        }
    }

    private static Document ToLuceneDocument(MeetingSearchDocument source)
    {
        var document = new Document
        {
            new StringField(TenantField, source.TenantId.ToString("N"), Field.Store.NO),
            new StringField("meeting_id", source.MeetingId.ToString("D"), Field.Store.YES),
            new StoredField("meeting_title", source.MeetingTitle),
            new StoredField("meeting_number", source.MeetingNumber ?? string.Empty),
            new StoredField("kind", source.Kind),
            new StoredField("source_id", source.SourceId?.ToString("D") ?? string.Empty),
            new StoredField("source_label", source.SourceLabel ?? string.Empty),
            new StringField("source_key", $"{source.MeetingId:N}:{source.Kind}:{source.SourceId?.ToString("N") ?? "none"}", Field.Store.YES),
            new StoredField("content_stored", PersianSearchText.Normalize(source.Content)),
            new StoredField("updated_ticks", source.UpdatedAtUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new TextField(ContentField, PersianSearchText.Normalize(source.Content), Field.Store.NO),
        };
        return document;
    }

    private static string Stored(Document document, string name) => document.GetField(name)?.GetStringValue(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
            _analyzer.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class PersianAnalyzer : Analyzer
    {
        protected override TokenStreamComponents CreateComponents(string fieldName, System.IO.TextReader reader)
        {
            var tokenizer = new StandardTokenizer(Version, reader);
            TokenStream stream = new LowerCaseFilter(Version, tokenizer);
            return new TokenStreamComponents(tokenizer, stream);
        }
    }
}
