using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wendmem.Services;
using Wendmem.Storage;
using Wendmem.Wiki;

namespace Wendmem.Tests;

public sealed class CompoundingLoopTests
{
    string _dbPath = null!;
    DuckDbConnectionFactory _factory = null!;
    readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    [Before(Test)]
    public void Init()
    {
        _dbPath = Path.GetTempFileName() + ".duckdb";
        _factory = new DuckDbConnectionFactory(_dbPath);
    }

    [After(Test)]
    public async Task Cleanup()
    {
        await _factory.DisposeAsync();
        try
        { File.Delete(_dbPath); }
        catch { }
    }

    // --- ActivityLog ---

    [Test]
    public async Task ActivityLog_LogsAndRetrieves()
    {
        var log = new ActivityLog(_factory, _loggerFactory.CreateLogger<ActivityLog>());

        await log.LogAsync("mine", "work", "/some/path", null, "10 drawers added");
        await log.LogAsync("wiki_write", "work", "my-page", "goose", "Created my-page");

        var entries = await log.RecentAsync(null, 10);
        await Assert.That(entries).HasCount(2);
        await Assert.That(entries[0].Action).IsEqualTo("wiki_write");
        await Assert.That(entries[1].Action).IsEqualTo("mine");
        await Assert.That(entries[0].Wing).IsEqualTo("work");
        await Assert.That(entries[0].Target).IsEqualTo("my-page");
        await Assert.That(entries[0].Agent).IsEqualTo("goose");
    }

    [Test]
    public async Task ActivityLog_FiltersByWing()
    {
        var log = new ActivityLog(_factory, _loggerFactory.CreateLogger<ActivityLog>());

        await log.LogAsync("mine", "work", null, null, "work stuff");
        await log.LogAsync("mine", "personal", null, null, "personal stuff");

        var workEntries = await log.RecentAsync("work", 10);
        await Assert.That(workEntries).HasCount(1);
        await Assert.That(workEntries[0].Wing).IsEqualTo("work");
    }

    [Test]
    public async Task ActivityLog_EmptyState()
    {
        var log = new ActivityLog(_factory, _loggerFactory.CreateLogger<ActivityLog>());
        var entries = await log.RecentAsync(null, 10);
        await Assert.That(entries).HasCount(0);
    }

    // --- PendingUpdateService ---

    [Test]
    public async Task PendingUpdate_EmptyState()
    {
        var svc = new PendingUpdateService(_factory, new FakeEmbedder(), _loggerFactory.CreateLogger<PendingUpdateService>());
        var result = await svc.ListPendingAsync(null, null, 10);
        await Assert.That(result).HasCount(0);
    }

    [Test]
    public async Task PendingUpdate_QueueAndList()
    {
        var embedder = new FakeEmbedder();
        var svc = new PendingUpdateService(_factory, embedder, _loggerFactory.CreateLogger<PendingUpdateService>());

        await CreateWikiPageAsync("test-page", "work", "Test Page", "Some content about testing");

        var storage = MakeStorage(embedder);
        var drawerId = (await storage.AddDrawerAsync("Content about testing", "work", "test", null, null)).Id;

        await svc.QueueAsync([drawerId], "work", threshold: 0.5f);

        var pending = await svc.ListPendingAsync("work", null, 10);
        await Assert.That(pending).HasCount(1);
        await Assert.That(pending[0].PagePath).IsEqualTo("test-page");
        await Assert.That(pending[0].DrawerId).IsEqualTo(drawerId);
    }

    [Test]
    public async Task PendingUpdate_ResolveDismissed()
    {
        var embedder = new FakeEmbedder();
        var svc = new PendingUpdateService(_factory, embedder, _loggerFactory.CreateLogger<PendingUpdateService>());

        await CreateWikiPageAsync("page-a", "work", "Page A", "Content A");

        var storage = MakeStorage(embedder);
        var drawerId = (await storage.AddDrawerAsync("Content A", "work", "test", null, null)).Id;
        await svc.QueueAsync([drawerId], "work", threshold: 0.5f);
        await svc.ResolveAsync("page-a", drawerId, "dismissed");

        var pending = await svc.ListPendingAsync("work", null, 10);
        await Assert.That(pending).HasCount(0);
    }

    [Test]
    public async Task PendingUpdate_ResolveForCitations()
    {
        var embedder = new FakeEmbedder();
        var svc = new PendingUpdateService(_factory, embedder, _loggerFactory.CreateLogger<PendingUpdateService>());

        await CreateWikiPageAsync("my-page", "work", "My Page", "Some content");

        var storage = MakeStorage(embedder);
        var d1 = (await storage.AddDrawerAsync("Some content", "work", "test", null, null)).Id;
        await svc.QueueAsync([d1], "work", threshold: 0.5f);
        await svc.ResolveForCitationsAsync("my-page", [d1], "updated");

        var pending = await svc.ListPendingAsync("work", null, 10);
        await Assert.That(pending).HasCount(0);
    }

    [Test]
    public async Task PendingUpdate_Summary()
    {
        var embedder = new FakeEmbedder();
        var svc = new PendingUpdateService(_factory, embedder, _loggerFactory.CreateLogger<PendingUpdateService>());

        await CreateWikiPageAsync("page-a", "work", "Page A", "Content A");
        await CreateWikiPageAsync("page-b", "work", "Page B", "Content B");

        var storage = MakeStorage(embedder);
        var d1 = (await storage.AddDrawerAsync("Content A", "work", "test", null, null)).Id;
        var d2 = (await storage.AddDrawerAsync("Content B", "work", "test", null, null)).Id;
        await svc.QueueAsync([d1, d2], "work", threshold: 0.5f);

        var summary = await svc.SummaryAsync("work");
        await Assert.That(summary.Count).IsEqualTo(2);
        await Assert.That(summary["page-a"]).IsGreaterThanOrEqualTo(1);
        await Assert.That(summary["page-b"]).IsGreaterThanOrEqualTo(1);
    }

    // --- WikiLinter ---

    [Test]
    public async Task WikiLinter_EmptyState()
    {
        var embedder = new FakeEmbedder();
        var pending = new PendingUpdateService(_factory, embedder, _loggerFactory.CreateLogger<PendingUpdateService>());
        var linter = new WikiLinter(_factory, pending, _loggerFactory.CreateLogger<WikiLinter>());

        var report = await linter.LintAsync(null);
        await Assert.That(report.PageCount).IsEqualTo(0);
        await Assert.That(report.Findings.Count).IsEqualTo(0);
    }

    [Test]
    public async Task WikiLinter_BrokenCitation()
    {
        var embedder = new FakeEmbedder();
        var pending = new PendingUpdateService(_factory, embedder, _loggerFactory.CreateLogger<PendingUpdateService>());
        var linter = new WikiLinter(_factory, pending, _loggerFactory.CreateLogger<WikiLinter>());

        await CreateWikiPageWithCitationsAsync("broken-test", "work", "Broken", "Content", ["deadbeef12345678"]);

        var report = await linter.LintAsync("work");
        var broken = report.Findings.FirstOrDefault(f => f.Rule == "BrokenCitation");
        await Assert.That(broken).IsNotNull();
    }

    [Test]
    public async Task WikiLinter_OrphanPage()
    {
        var embedder = new FakeEmbedder();
        var pending = new PendingUpdateService(_factory, embedder, _loggerFactory.CreateLogger<PendingUpdateService>());
        var linter = new WikiLinter(_factory, pending, _loggerFactory.CreateLogger<WikiLinter>());

        await CreateWikiPageAsync("lonely-page", "work", "Lonely Page", "Just content, no links.");

        var report = await linter.LintAsync("work");
        var orphan = report.Findings.FirstOrDefault(f => f.Rule == "OrphanPage" && f.PagePath == "lonely-page");
        await Assert.That(orphan).IsNotNull();
    }

    [Test]
    public async Task WikiLinter_StalePage()
    {
        var embedder = new FakeEmbedder();
        var pending = new PendingUpdateService(_factory, embedder, _loggerFactory.CreateLogger<PendingUpdateService>());
        var linter = new WikiLinter(_factory, pending, _loggerFactory.CreateLogger<WikiLinter>());

        // Create a drawer, then retire it manually
        var storage = MakeStorage(embedder);
        var drawerId = (await storage.AddDrawerAsync("Old content", "work", "test", null, null)).Id;
        await _factory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE drawers SET is_representative = FALSE WHERE id = $id";
            cmd.Parameters.Add(new DuckDBParameter("id", drawerId));
            await cmd.ExecuteNonQueryAsync();
        });

        // Page cites the retired drawer
        await CreateWikiPageWithCitationsAsync("stale-page", "work", "Stale", "Old info", [drawerId]);

        var report = await linter.LintAsync("work");
        var stale = report.Findings.FirstOrDefault(f => f.Rule == "StalePage" && f.PagePath == "stale-page");
        await Assert.That(stale).IsNotNull();
    }

    [Test]
    public async Task WikiLinter_MissingCrossLink()
    {
        var embedder = new FakeEmbedder();
        var pending = new PendingUpdateService(_factory, embedder, _loggerFactory.CreateLogger<PendingUpdateService>());
        var linter = new WikiLinter(_factory, pending, _loggerFactory.CreateLogger<WikiLinter>());

        // Page A mentions Page B's title but doesn't wikilink to it
        await CreateWikiPageAsync("page-a", "work", "Alpha", "I read about Beta recently.");
        await CreateWikiPageAsync("page-b", "work", "Beta", "All about beta.");

        var report = await linter.LintAsync("work");
        var missing = report.Findings.FirstOrDefault(
            f => f.Rule == "MissingCrossLink" && f.PagePath == "page-a");
        await Assert.That(missing).IsNotNull();
    }

    [Test]
    public async Task WikiLinter_PendingUpdatesRule()
    {
        var embedder = new FakeEmbedder();
        var pending = new PendingUpdateService(_factory, embedder, _loggerFactory.CreateLogger<PendingUpdateService>());
        var linter = new WikiLinter(_factory, pending, _loggerFactory.CreateLogger<WikiLinter>());

        // Create a page and 3+ drawers that match it
        await CreateWikiPageAsync("popular-page", "work", "Popular", "Shared content here");

        var storage = MakeStorage(embedder);
        var ids = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            var id = (await storage.AddDrawerAsync($"Shared content here variant {i}", "work", "test", null, null)).Id;
            ids.Add(id);
        }

        await pending.QueueAsync(ids, "work", threshold: 0.5f);

        var report = await linter.LintAsync("work");
        var pu = report.Findings.FirstOrDefault(
            f => f.Rule == "PendingUpdates" && f.PagePath == "popular-page");
        await Assert.That(pu).IsNotNull();
        await Assert.That(pu.Details["pending_count"]).IsNotNull();
    }

    [Test]
    public async Task WikiLinter_GapCandidate()
    {
        var embedder = new FakeEmbedder();
        var pending = new PendingUpdateService(_factory, embedder, _loggerFactory.CreateLogger<PendingUpdateService>());
        var linter = new WikiLinter(_factory, pending, _loggerFactory.CreateLogger<WikiLinter>());

        // Create an entity with 5+ triples but no wiki page
        var kg = new KnowledgeGraph(_factory);
        await kg.AddEntityAsync("rust-lang", "language", null, CancellationToken.None);
        for (int i = 0; i < 6; i++)
        {
            await kg.AddTripleAsync("rust-lang", $"property-{i}", $"value-{i}");
        }

        // No wiki page for "rust-lang" — should be a gap
        var report = await linter.LintAsync("work");
        var gap = report.Findings.FirstOrDefault(f => f.Rule == "GapCandidate");
        await Assert.That(gap).IsNotNull();
        await Assert.That(gap.Details["entity"]!.ToString()).Contains("rust-lang");
    }

    // --- Helpers ---

    DrawerStorage MakeStorage(FakeEmbedder embedder) => new(
        _factory, embedder,
        new ClosetStorage(_factory),
        new AaakDialect(new Dictionary<string, string>()),
        new EntityIndexService(_factory),
        new KnowledgeGraph(_factory),
        new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 500 }),
        new PalaceConfig { AdmissionEnabled = false });

    async Task CreateWikiPageAsync(string path, string wing, string title, string content)
    {
        var embedder = new FakeEmbedder();
        var vec = await embedder.EmbedAsync(content);
        var lit = Services.EmbeddingUtils.ToFloatArrayLiteral(vec);
        await _factory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO wiki_pages (path, wing, title, content, citations, updated_at, embedding)
                VALUES ($path, $wing, $title, $content, [], now(), {lit}::FLOAT[512])
                """;
            cmd.Parameters.Add(new DuckDBParameter("path", path));
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
            cmd.Parameters.Add(new DuckDBParameter("title", title));
            cmd.Parameters.Add(new DuckDBParameter("content", content));
            await cmd.ExecuteNonQueryAsync();
        });
    }

    async Task CreateWikiPageWithCitationsAsync(string path, string wing, string title, string content, string[] citations)
    {
        await _factory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            var citLit = "[" + string.Join(",", citations.Select(c => $"'{c}'")) + "]";
            cmd.CommandText = $"""
                INSERT INTO wiki_pages (path, wing, title, content, citations, updated_at)
                VALUES ($path, $wing, $title, $content, {citLit}, now())
                """;
            cmd.Parameters.Add(new DuckDBParameter("path", path));
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
            cmd.Parameters.Add(new DuckDBParameter("title", title));
            cmd.Parameters.Add(new DuckDBParameter("content", content));
            await cmd.ExecuteNonQueryAsync();
        });
    }

    /// <summary>
    /// Returns a fixed 512-element vector for every call. All inputs get the same embedding,
    /// so cosine similarity between any two items is 1.0.
    /// </summary>
    sealed class FakeEmbedder : IEmbedder
    {
        public int EmbeddingDimension => 512;
        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var v = new float[512];
            v[0] = 1.0f;
            return new ValueTask<float[]>(v);
        }
    }
}
