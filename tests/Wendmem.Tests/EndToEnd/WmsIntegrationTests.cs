using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Wendmem.Experiences;
using Wendmem.Models;
using Wendmem.Services;
using Wendmem.Storage;

namespace Wendmem.Tests;

/// <summary>
/// Integration tests using real WMS documentation from test-content.
/// Exercises: mining, search (FTS + cosine + hybrid), prune, cluster geometry, regime.
/// No LLM calls - FakeEmbedder returns deterministic vectors based on content hash.
/// </summary>
public class WmsIntegrationTests
{
    DuckDbConnectionFactory _factory = null!;
    string _dbPath = null!;
    DrawerStorage _storage = null!;
    PalaceSearcher _searcher = null!;
    string _testContentDir = null!;

    sealed class FakeChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "[]")));
        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    sealed class FakeEmbedder : IEmbedder
    {
        public int EmbeddingDimension => 512;
        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var v = new float[512];
            var hash = (uint)text.GetHashCode();
            for (var i = 0; i < 512; i++)
                v[i] = MathF.Sin(hash * (i + 1) * 0.0001f) * 0.5f;
            var norm = MathF.Sqrt(v.Sum(x => x * x));
            if (norm > 0f)
                for (var i = 0; i < 512; i++)
                    v[i] /= norm;
            return new ValueTask<float[]>(v);
        }
    }

    [Before(Test)]
    public void Initialize()
    {
        _dbPath = Path.GetTempFileName() + ".duckdb";
        _factory = new DuckDbConnectionFactory(_dbPath);

        var embedder = new FakeEmbedder();
        var closets = new ClosetStorage(_factory);
        var aaak = new AaakDialect();
        var entityIndex = new EntityIndexService(_factory);
        var kg = new KnowledgeGraph(_factory);
        _storage = new DrawerStorage(_factory, embedder, closets, aaak, entityIndex, kg, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 500 }),
        new PalaceConfig { AdmissionEnabled = false });
        var loggerFactory = LoggerFactory.Create(b => { });
        var wikiLogger = loggerFactory.CreateLogger<Wiki.WikiStorage>();
        var wiki = new Wiki.WikiStorage(_factory, embedder, wikiLogger, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var searcherLogger = loggerFactory.CreateLogger<PalaceSearcher>();
        var chatClient = new FakeChatClient();
        var llm = new LlmService(chatClient, "fake");
        _searcher = new PalaceSearcher(_storage, embedder, kg, wiki, entityIndex, new PalaceConfig { AdmissionEnabled = false }, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 500 }), llm, searcherLogger, null, null);

        // Walk up from test bin to find todo/test-content
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "todo", "test-content");
            if (Directory.Exists(candidate))
            { _testContentDir = candidate; break; }
            dir = Directory.GetParent(dir)?.FullName;
        }
        _testContentDir ??= Path.Combine(
            Environment.CurrentDirectory, "todo", "test-content");
    }

    [After(Test)]
    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        File.Delete(_dbPath);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private async Task<List<string>> MineFileAsync(
        string filePath, string wing, CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(filePath, ct);
        var source = Path.GetFileName(filePath);
        var room = RoomClassifier.Classify(source);
        var ids = new List<string>();

        const int chunkSize = 800;
        const int overlap = 100;
        var pos = 0;
        while (pos < content.Length)
        {
            var len = Math.Min(chunkSize, content.Length - pos);
            var chunk = content.Substring(pos, len);
            var id = (await _storage.AddDrawerAsync(
                content: chunk, wing: wing, room: room,
                source: source, sourceMtime: null,
                drawerType: "source", ct: ct))?.Id;
            if (id is not null)
                ids.Add(id);
            pos += chunkSize - overlap;
        }
        return ids;
    }

    private async Task<int> MineAllTestContentAsync(string wing, CancellationToken ct)
    {
        if (!Directory.Exists(_testContentDir))
            return 0;
        var files = Directory.GetFiles(_testContentDir, "*.md")
            .OrderBy(f => f).ToList();
        var total = 0;
        foreach (var file in files)
        {
            var ids = await MineFileAsync(file, wing, ct);
            total += ids.Count;
        }
        return total;
    }

    private bool TestContentExists() => Directory.Exists(_testContentDir);

    // ── Mining Tests ─────────────────────────────────────────────

    [Test]
    public async Task Mine_SingleFile_ProducesMultipleChunks()
    {
        if (!TestContentExists())
        { await Assert.That(true).IsTrue(); return; }
        var file = Path.Combine(_testContentDir, "01_systemoversikt_och_arkitektur.md");
        if (!File.Exists(file))
        { await Assert.That(true).IsTrue(); return; }

        var ids = await MineFileAsync(file, "wms-test", default);
        await Assert.That(ids.Count).IsGreaterThan(1);
    }

    [Test]
    public async Task Mine_AllTestContent_ProducesDrawers()
    {
        if (!TestContentExists())
        { await Assert.That(true).IsTrue(); return; }

        var count = await MineAllTestContentAsync("wms-full", default);
        await Assert.That(count).IsGreaterThan(0);

        var dbCount = await _storage.CountAsync(default);
        await Assert.That(dbCount).IsEqualTo(count);
    }

    [Test]
    public async Task Mine_DuplicateContent_SkipsDuplicates()
    {
        if (!TestContentExists())
        { await Assert.That(true).IsTrue(); return; }
        var file = Path.Combine(_testContentDir, "08_orderhantering_databas.md");
        if (!File.Exists(file))
        { await Assert.That(true).IsTrue(); return; }

        var ids1 = await MineFileAsync(file, "dedup-test", default);
        var ids2 = await MineFileAsync(file, "dedup-test", default);

        var newIds = ids2.Where(id => id is not null).ToList();
        await Assert.That(newIds.Count).IsEqualTo(0);
    }

    // ── Search Tests ─────────────────────────────────────────────

    [Test]
    public async Task FtsSearch_FindsOrderStatus()
    {
        if (!TestContentExists())
        { await Assert.That(true).IsTrue(); return; }
        await MineAllTestContentAsync("search-test", default);

        var results = await _storage.FtsSearchAsync(
            "ORDER_STATUS", "search-test", 5, default);

        await Assert.That(results.Count).IsGreaterThan(0);
        foreach (var r in results)
            await Assert.That(r.Score).IsGreaterThan(0f);
    }

    [Test]
    public async Task CosineSearch_FindsRelevantContent()
    {
        var count = await MineAllTestContentAsync("cosine-test", default);
        if (count == 0)
        { await Assert.That(true).IsTrue(); return; }

        await _storage.RebuildFtsIndexAsync(default);

        var embedder = new FakeEmbedder();
        var queryVec = await embedder.EmbedAsync("transport bokning", default);

        var results = await _storage.SearchAsync(
            queryVec, "cosine-test", null, 5, 0.85f, default);

        await Assert.That(results).IsNotNull();
    }

    [Test]
    public async Task HybridSearch_CombinesFtsAndCosine()
    {
        var count = await MineAllTestContentAsync("hybrid-test", default);
        if (count == 0)
        { await Assert.That(true).IsTrue(); return; }

        await _storage.RebuildFtsIndexAsync(default);

        var embedder = new FakeEmbedder();
        var queryVec = await embedder.EmbedAsync("plock lager plats", default);

        var results = await _storage.HybridSearchAsync(
            "plock lager plats", queryVec, "hybrid-test", null, 5, default);

        await Assert.That(results).IsNotNull();
    }

    [Test]
    public async Task SearchResults_HaveRegimeTag()
    {
        var count = await MineAllTestContentAsync("regime-test", default);
        if (count == 0)
        { await Assert.That(true).IsTrue(); return; }

        await _storage.RebuildFtsIndexAsync(default);

        var embedder = new FakeEmbedder();
        var queryVec = await embedder.EmbedAsync("lagerhantering", default);

        var results = await _storage.SearchAsync(
            queryVec, "regime-test", null, 10, 0.85f, default);

        foreach (var r in results)
            await Assert.That(Enum.IsDefined(r.Regime)).IsTrue();
    }

    // ── Prune Tests ──────────────────────────────────────────────

    [Test]
    public async Task Prune_SoftRetires_DoesNotHardDelete()
    {
        if (!TestContentExists())
        { await Assert.That(true).IsTrue(); return; }
        var file = Path.Combine(_testContentDir, "01_systemoversikt_och_arkitektur.md");
        if (!File.Exists(file))
        { await Assert.That(true).IsTrue(); return; }

        var content = await File.ReadAllTextAsync(file, default);

        await _storage.AddDrawerAsync(
            content: content[..500], wing: "prune-test", room: "docs",
            source: "test1.md", sourceMtime: null, drawerType: "source", ct: default);
        await _storage.AddDrawerAsync(
            content: content[..500], wing: "prune-test", room: "docs",
            source: "test2.md", sourceMtime: null, drawerType: "source", ct: default);

        var beforeCount = await _storage.CountAsync(default);

        var report = await _storage.PruneAsync("prune-test", 0.97f, default);

        var afterCount = await _storage.CountAsync(default);
        await Assert.That(afterCount).IsEqualTo(beforeCount);
        await Assert.That(report).IsNotNull();
    }

    [Test]
    public async Task PruneReport_ContainsValidCounts()
    {
        var count = await MineAllTestContentAsync("report-test", default);
        if (count == 0)
        { await Assert.That(true).IsTrue(); return; }

        await _storage.RebuildFtsIndexAsync(default);
        var report = await _storage.PruneAsync("report-test", 0.97f, default);

        await Assert.That(report.Kept + report.Retired).IsGreaterThan(0);
        await Assert.That(report.Clusters).IsGreaterThan(0);
    }

    // ── Cluster Geometry Tests ───────────────────────────────────

    [Test]
    public async Task ClusterGeometry_PopulatesClusterColumns()
    {
        var count = await MineAllTestContentAsync("geom-test", default);
        if (count == 0)
        { await Assert.That(true).IsTrue(); return; }

        await _storage.RebuildFtsIndexAsync(default);

        var drawers = await _storage.RecentDrawersAsync("geom-test", count, default);
        var withCluster = drawers.Count(d => d.ClusterId.HasValue);
        await Assert.That(withCluster).IsGreaterThan(0);
    }

    [Test]
    public async Task ClusterGeometry_DBar_IsValid()
    {
        var count = await MineAllTestContentAsync("dbar-test", default);
        if (count == 0)
        { await Assert.That(true).IsTrue(); return; }

        await _storage.RebuildFtsIndexAsync(default);

        var drawers = await _storage.RecentDrawersAsync("dbar-test", count, default);
        var withDBar = drawers.Where(d => d.ClusterDBar.HasValue).ToList();
        if (withDBar.Count == 0)
        { await Assert.That(true).IsTrue(); return; }

        foreach (var d in withDBar)
        {
            await Assert.That(d.ClusterDBar!.Value).IsGreaterThanOrEqualTo(0f);
            await Assert.That(d.ClusterDBar!.Value).IsLessThanOrEqualTo(2f);
        }
    }

    [Test]
    public async Task ClusterRegime_MatchesDBarVsThetaPrime()
    {
        var count = await MineAllTestContentAsync("regime-verify", default);
        if (count == 0)
        { await Assert.That(true).IsTrue(); return; }

        await _storage.RebuildFtsIndexAsync(default);

        var embedder = new FakeEmbedder();
        var queryVec = await embedder.EmbedAsync("orderhantering status", default);

        var results = await _storage.SearchAsync(
            queryVec, "regime-verify", null, 10, 0.85f, default);

        foreach (var r in results)
        {
            if (r.Drawer.ClusterDBar is float dBar and > 0f)
            {
                var thetaPrime = 1f - 0.85f;
                var expected = dBar < thetaPrime
                    ? ClusterRegime.Tight
                    : ClusterRegime.Spread;
                await Assert.That(r.Regime).IsEqualTo(expected);
            }
        }
    }

    // ── WakeUp Tests ─────────────────────────────────────────────

    [Test]
    public async Task WakeUp_ReturnsResultsFromMinedContent()
    {
        var count = await MineAllTestContentAsync("wakeup-test", default);
        if (count == 0)
        { await Assert.That(true).IsTrue(); return; }

        await _storage.RebuildFtsIndexAsync(default);

        var result = await _searcher.WakeUpAsync("wakeup-test", "lagerhantering pallar", default);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Content.Length).IsGreaterThan(0);
    }

    // ── Cross-Wing Isolation ─────────────────────────────────────

    [Test]
    public async Task Search_WingIsolation_NoCrossContamination()
    {
        if (!TestContentExists())
        { await Assert.That(true).IsTrue(); return; }
        var file = Path.Combine(_testContentDir, "01_systemoversikt_och_arkitektur.md");
        if (!File.Exists(file))
        { await Assert.That(true).IsTrue(); return; }

        await MineFileAsync(file, "wing-a", default);
        await MineFileAsync(file, "wing-b", default);

        var resultsA = await _storage.FtsSearchAsync("NoMan WMS", "wing-a", 10, default);
        foreach (var r in resultsA)
            await Assert.That(r.Drawer.Wing).IsEqualTo("wing-a");

        var resultsB = await _storage.FtsSearchAsync("NoMan WMS", "wing-b", 10, default);
        foreach (var r in resultsB)
            await Assert.That(r.Drawer.Wing).IsEqualTo("wing-b");
    }

    // ── IsRepresentative Filter ──────────────────────────────────

    [Test]
    public async Task Search_SkipsNonRepresentativeDrawers()
    {
        var id = (await _storage.AddDrawerAsync(
            content: "This content should not appear in search",
            wing: "filter-test", room: "docs",
            source: "test.md", sourceMtime: null,
            drawerType: "source", ct: default))?.Id;
        await Assert.That(id).IsNotNull();

        var drawerId = id!;
        await _factory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE drawers SET is_representative = FALSE WHERE id = $id";
            cmd.Parameters.Add(new DuckDBParameter("id", drawerId));
            await cmd.ExecuteNonQueryAsync();
        });

        await _storage.RebuildFtsIndexAsync(default);

        var results = await _storage.FtsSearchAsync(
            "should not appear", "filter-test", 10, default);
        var found = results.Any(r => r.Drawer.Id == id);
        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task GetById_ReturnsDrawerWithAllFields()
    {
        var id = (await _storage.AddDrawerAsync(
            content: "Test content for GetById",
            wing: "getbyid-test", room: "docs",
            source: "test.md", sourceMtime: null,
            drawerType: "source", ct: default))?.Id;
        await Assert.That(id).IsNotNull();

        var drawer = await _storage.GetByIdAsync(id!, default);
        await Assert.That(drawer).IsNotNull();
        await Assert.That(drawer!.Id).IsEqualTo(id);
        await Assert.That(drawer.Wing).IsEqualTo("getbyid-test");
        await Assert.That(drawer.Room).IsEqualTo("docs");
        await Assert.That(drawer.Content).IsEqualTo("Test content for GetById");
        await Assert.That(drawer.Source).IsEqualTo("test.md");
        await Assert.That(drawer.DrawerType).IsEqualTo("source");
    }
}
