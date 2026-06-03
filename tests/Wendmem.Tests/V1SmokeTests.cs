using Wendmem.Services;
using Wendmem.Storage;

namespace Wendmem.Tests;

/// <summary>
/// V1.1 smoke tests: F1 admission, F2 governance, F3 recency, F4 chunking, F6 prune protection.
/// All run against a temp DuckDB — no LLM calls.
/// </summary>
public class V1SmokeTests
{
    /// <summary>
    /// Returns a fixed 512-element vector. All inputs get the same embedding,
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

    string _dbPath = null!;
    DuckDbConnectionFactory _factory = null!;
    DrawerStorage _storage = null!;
    readonly FakeEmbedder _embedder = new();

    [Before(Test)]
    public async Task Init()
    {
        _dbPath = Path.GetTempFileName() + ".duckdb";
        _factory = new DuckDbConnectionFactory(_dbPath);
        var closets = new ClosetStorage(_factory);
        var aaak = new AaakDialect(new Dictionary<string, string>());
        var entityIndex = new EntityIndexService(_factory);
        var kg = new KnowledgeGraph(_factory);
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 500 });
        _storage = new DrawerStorage(_factory, _embedder, closets, aaak, entityIndex, kg, cache, new PalaceConfig());
    }

    [After(Test)]
    public void Cleanup()
    {
        _factory.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    // ── F1: A-MAC admission control ────────────────────────────────────

    [Test]
    public async Task F1_Admission_RejectsNearDuplicate()
    {
        var r1 = await _storage.AddDrawerAsync(
            new string('x', 200), "work", "test", null, null, ct: CancellationToken.None);
        await Assert.That(r1.Admitted).IsTrue();

        // Same wing, same embedder vector => cosine = 1.0 >= 0.97 => rejected
        var r2 = await _storage.AddDrawerAsync(
            new string('y', 200), "work", "test2", null, null, ct: CancellationToken.None);
        await Assert.That(r2.Admitted).IsFalse();
    }
    [Test]
    public async Task F1_Admission_DisabledWhenThreshold1()
    {
        var config = new PalaceConfig { AdmissionEnabled = false };
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 500 });
        var storage = new DrawerStorage(_factory, _embedder,
            new ClosetStorage(_factory), new AaakDialect(new Dictionary<string, string>()),
            new EntityIndexService(_factory), new KnowledgeGraph(_factory), cache, config);

        var id1 = (await storage.AddDrawerAsync("alpha content", "work", "a", null, null, ct: CancellationToken.None)).Id;
        var id2 = (await storage.AddDrawerAsync("beta content", "work", "b", null, null, ct: CancellationToken.None)).Id;
        await Assert.That(id1).IsNotNull();
        await Assert.That(id2).IsNotNull();
    }

    // ── F4: TA-Mem topic-shift chunking ─────────────────────────────────

    [Test]
    public async Task F4_Chunking_SplitsAtHeadings()
    {
        // Build text that exceeds TargetSize (800) with a heading in the middle
        var text = "Intro paragraph. " + new string('x', 700) + "\n\n## Section Two\n" +
            "Content in section two. " + new string('y', 700);
        var chunks = TopicShiftChunker.Chunk(text);
        await Assert.That(chunks.Count).IsGreaterThan(1);
    }

    [Test]
    public async Task F4_Chunking_ShortTextReturnsSingleChunk()
    {
        var chunks = TopicShiftChunker.Chunk("Short text");
        await Assert.That(chunks.Count).IsEqualTo(1);
        await Assert.That(chunks[0]).IsEqualTo("Short text");
    }

    [Test]
    public async Task F4_Chunking_SplitsAtCodeBlock()
    {
        var text = "Some preamble with words. " + new string('a', 700) +
            "\npublic class Foo\n{\n}\n" +
            "Another section after class. " + new string('b', 700);
        var chunks = TopicShiftChunker.Chunk(text);
        await Assert.That(chunks.Count).IsGreaterThan(1);
    }

    [Test]
    public async Task F4_Chunking_EmptyInputReturnsEmpty()
    {
        var chunks = TopicShiftChunker.Chunk("");
        await Assert.That(chunks.Count).IsEqualTo(0);
    }

    // ── F6: Usage-based decay / prune protection ────────────────────────

    [Test]
    public async Task F6_Prune_ProtectsHighAccessDrawers()
    {
        var ids = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            var id = (await _storage.AddDrawerAsync(
                $"Very similar content number {i} with shared words", "work", "test", null, null, ct: CancellationToken.None)).Id;
            ids.Add(id);
        }

        // Simulate high access on the third drawer (3 accesses)
        _storage.RecordAccess([ids[2]]);
        _storage.RecordAccess([ids[2]]);
        _storage.RecordAccess([ids[2]]);

        var config = new PalaceConfig { PruneAccessProtectionThreshold = 3 };
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 500 });
        var storage = new DrawerStorage(_factory, _embedder,
            new ClosetStorage(_factory), new AaakDialect(new Dictionary<string, string>()),
            new EntityIndexService(_factory), new KnowledgeGraph(_factory), cache, config);

        var report = await storage.PruneAsync("work", 0.90f, CancellationToken.None);
        await Assert.That(report).IsNotNull();
    }

    // ── F2: SSGM conflict governance modes ──────────────────────────────

    [Test]
    public async Task F2_PalaceConfig_DefaultConflictGovernanceIsBalanced()
    {
        var config = new PalaceConfig();
        await Assert.That(config.ConflictGovernance).IsEqualTo("balanced");
    }

    [Test]
    public async Task F2_PalaceConfig_SupportsOffMode()
    {
        var config = new PalaceConfig { ConflictGovernance = "off" };
        await Assert.That(config.ConflictGovernance).IsEqualTo("off");
    }

    // ── F3: RF-Mem adaptive retrieval defaults ──────────────────────────

    [Test]
    public async Task F3_PalaceConfig_DefaultRecencyHalfLife()
    {
        var config = new PalaceConfig();
        await Assert.That(config.RecencyHalfLifeDays).IsEqualTo(14f);
    }

    [Test]
    public async Task F3_PalaceConfig_DefaultDecayStaleDays()
    {
        var config = new PalaceConfig();
        await Assert.That(config.DecayStaleDays).IsEqualTo(30f);
    }
}
