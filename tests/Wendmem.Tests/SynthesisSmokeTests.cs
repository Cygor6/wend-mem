using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Wendmem.Experiences;
using Wendmem.Services;
using Wendmem.Storage;
using Wendmem.Wiki;

namespace Wendmem.Tests;

/// <summary>
/// Smoke tests for the synthesis layer: drawer_type, wakeup prioritization,
/// RoomClassifier, UpsertDrawer, SaveSessionState semantics.
/// No LLM calls - FakeEmbedder returns zero vectors.
/// </summary>
public class SynthesisSmokeTests
{
    DuckDbConnectionFactory _factory = null!;
    string _dbPath = null!;
    DrawerStorage _storage = null!;
    PalaceSearcher _searcher = null!;

    sealed class FakeChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "[]")));
        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Returns a 512-element zero vector for every call.</summary>
    sealed class FakeEmbedder : IEmbedder
    {
        public int EmbeddingDimension => 512;
        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var v = new float[512];
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
        var wikiLogger = loggerFactory.CreateLogger<WikiStorage>();
        var wiki = new WikiStorage(_factory, embedder, wikiLogger, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var entityIndexSvc = new EntityIndexService(_factory);
        var searcherLogger = loggerFactory.CreateLogger<PalaceSearcher>();
        var chatClient = new FakeChatClient();
        var llm = new LlmService(chatClient, "fake");
        _searcher = new PalaceSearcher(_storage, embedder, kg, wiki, entityIndexSvc, new PalaceConfig { AdmissionEnabled = false }, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 500 }), llm, searcherLogger, null, null);
    }

    [After(Test)]
    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        File.Delete(_dbPath);
    }

    // ── RoomClassifier (pure unit tests, no IO) ──────────────────────────

    [Test]
    public async Task RoomClassifier_Controllers()
    {
        await Assert.That(RoomClassifier.Classify("/src/controllers/UserController.cs"))
            .IsEqualTo("controllers");
    }

    [Test]
    public async Task RoomClassifier_Services()
    {
        await Assert.That(RoomClassifier.Classify("/src/services/AuthService.cs"))
            .IsEqualTo("services");
    }

    [Test]
    public async Task RoomClassifier_Models()
    {
        await Assert.That(RoomClassifier.Classify("/src/models/User.cs"))
            .IsEqualTo("models");
    }

    [Test]
    public async Task RoomClassifier_Migrations()
    {
        await Assert.That(RoomClassifier.Classify("/src/migrations/001_init.sql"))
            .IsEqualTo("migrations");
    }

    [Test]
    public async Task RoomClassifier_TestFile()
    {
        await Assert.That(RoomClassifier.Classify("UserController.test.ts"))
            .IsEqualTo("tests");
    }

    [Test]
    public async Task RoomClassifier_Default_Is_Source()
    {
        await Assert.That(RoomClassifier.Classify("/src/utils/StringHelper.cs"))
            .IsEqualTo("source");
    }

    [Test]
    public async Task RoomClassifier_WindowsPath()
    {
        await Assert.That(RoomClassifier.Classify("C:\\dev\\src\\Handlers\\Ping.cs"))
            .IsEqualTo("handlers");
    }

    // ── Schema: drawer_type column exists with CHECK ──────────────────────

    [Test]
    public async Task Schema_HasDrawerTypeColumn_WithCheckConstraint()
    {
        await using var ro = _factory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT column_name, data_type, column_default
            FROM information_schema.columns
            WHERE table_name = 'drawers' AND column_name = 'drawer_type'
            """;
        using var reader = cmd.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetString(0)).IsEqualTo("drawer_type");
    }

    [Test]
    public async Task Schema_DrawerType_RejectsInvalidValue()
    {
        await Assert.That(async () =>
            await _factory.ExecuteWriteAsync(async db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO drawers (id, wing, room, content, content_hash, drawer_type)
                    VALUES ('bad', 'w', 'r', 'c', 'h', 'invalid_type')
                    """;
                await Task.Run(() => cmd.ExecuteNonQuery());
            })).Throws<Exception>();
    }

    // ── AddDrawerAsync: source vs synthesis ───────────────────────────────

    [Test]
    public async Task AddDrawer_Default_Is_Source()
    {
        var id = (await _storage.AddDrawerAsync("source content", "test-wing", "room-a",
            source: null, sourceMtime: null, ct: default)).Id;

        await using var ro = _factory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = "SELECT drawer_type FROM drawers WHERE id = $id";
        cmd.Parameters.Add(new DuckDBParameter("id", id));
        var result = (string)cmd.ExecuteScalar()!;
        await Assert.That(result).IsEqualTo("source");
    }

    [Test]
    public async Task AddDrawer_Synthesis_Stores_Type()
    {
        var id = (await _storage.AddDrawerAsync("synthesis decision", "test-wing", "session",
            source: null, sourceMtime: null, drawerType: "synthesis", ct: default)).Id;

        await using var ro = _factory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = "SELECT drawer_type FROM drawers WHERE id = $id";
        cmd.Parameters.Add(new DuckDBParameter("id", id));
        var result = (string)cmd.ExecuteScalar()!;
        await Assert.That(result).IsEqualTo("synthesis");
    }

    [Test]
    public async Task AddDrawer_OnConflict_DoNothing()
    {
        var id1 = (await _storage.AddDrawerAsync("unique content", "w", "r",
            source: null, sourceMtime: null, ct: default)).Id;
        var id2 = (await _storage.AddDrawerAsync("unique content", "w", "r",
            source: null, sourceMtime: null, ct: default)).Id;

        await Assert.That(id1).IsEqualTo(id2);

        await using var ro = _factory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM drawers WHERE id = $id";
        cmd.Parameters.Add(new DuckDBParameter("id", id1));
        var count = (long)cmd.ExecuteScalar()!;
        await Assert.That(count).IsEqualTo(1L);
    }

    // ── UpsertDrawerAsync: replace on conflict ───────────────────────────

    [Test]
    public async Task UpsertDrawer_Inserts_WhenNew()
    {
        await _storage.UpsertDrawerAsync("upsert-new-id", "w", "session",
            "initial content", "key1", null, "synthesis", default);

        await using var ro = _factory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = "SELECT content, drawer_type FROM drawers WHERE id = $id";
        cmd.Parameters.Add(new DuckDBParameter("id", "upsert-new-id"));
        using var reader = cmd.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetString(0)).IsEqualTo("initial content");
        await Assert.That(reader.GetString(1)).IsEqualTo("synthesis");
    }

    [Test]
    public async Task UpsertDrawer_Replaces_OnConflict()
    {
        await _storage.UpsertDrawerAsync("upsert-replace-id", "w", "session",
            "version 1", "key1", null, "synthesis", default);
        await _storage.UpsertDrawerAsync("upsert-replace-id", "w", "session",
            "version 2", "key1", null, "synthesis", default);

        await using var ro = _factory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = "SELECT content, drawer_type FROM drawers WHERE id = $id";
        cmd.Parameters.Add(new DuckDBParameter("id", "upsert-replace-id"));
        using var reader = cmd.ExecuteReader();
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.GetString(0)).IsEqualTo("version 2");

        cmd.Parameters.Clear();
        cmd.CommandText = "SELECT count(*) FROM drawers WHERE id = 'upsert-replace-id'";
        var count = (long)cmd.ExecuteScalar()!;
        await Assert.That(count).IsEqualTo(1L);
    }

    // ── SynthesisDrawersAsync ─────────────────────────────────────────────

    [Test]
    public async Task SynthesisDrawers_Returns_OnlySynthesis()
    {
        await _storage.AddDrawerAsync("source chunk", "w", "r",
            source: null, sourceMtime: null, drawerType: "source", ct: default);
        await _storage.AddDrawerAsync("synthesis chunk", "w", "session",
            source: null, sourceMtime: null, drawerType: "synthesis", ct: default);

        var results = await _storage.SynthesisDrawersAsync("w", default);
        await Assert.That(results).Count().IsEqualTo(1);
        await Assert.That(results[0].DrawerType).IsEqualTo("synthesis");
        await Assert.That(results[0].Content).IsEqualTo("synthesis chunk");
    }

    [Test]
    public async Task SynthesisDrawers_FiltersByWing()
    {
        await _storage.AddDrawerAsync("synth-a", "wing-a", "session",
            source: null, sourceMtime: null, drawerType: "synthesis", ct: default);
        await _storage.AddDrawerAsync("synth-b", "wing-b", "session",
            source: null, sourceMtime: null, drawerType: "synthesis", ct: default);

        var results = await _storage.SynthesisDrawersAsync("wing-a", default);
        await Assert.That(results).Count().IsEqualTo(1);
        await Assert.That(results[0].Content).IsEqualTo("synth-a");
    }

    // ── WakeUpFull: synthesis-first prioritization ────────────────────────

    [Test]
    public async Task WakeUpFull_SynthesisAppearsFirst()
    {
        await _storage.AddDrawerAsync("source content about auth", "wakeup-wing", "source-room",
            source: null, sourceMtime: null, drawerType: "source", ct: default);
        await _storage.AddDrawerAsync("Decision: use JWT, not sessions", "wakeup-wing", "session",
            source: null, sourceMtime: null, drawerType: "synthesis", ct: default);

        var result = await _searcher.WakeUpFullAsync("wakeup-wing", seedQuery: null, default);

        var synthIdx = result.IndexOf("Decision: use JWT");
        var srcIdx = result.IndexOf("source content about auth");
        await Assert.That(synthIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(srcIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(synthIdx).IsLessThan(srcIdx);
    }

    [Test]
    public async Task WakeUpFull_SynthesisHasLabel()
    {
        await _storage.AddDrawerAsync("agent note", "label-wing", "session",
            source: null, sourceMtime: null, drawerType: "synthesis", ct: default);

        var result = await _searcher.WakeUpFullAsync("label-wing", seedQuery: null, default);

        await Assert.That(result).Contains("(synthesis)");
    }

    [Test]
    public async Task WakeUpFull_ReturnsNoContext_WhenEmpty()
    {
        var result = await _searcher.WakeUpFullAsync("empty-wing", seedQuery: null, default);
        await Assert.That(result).IsEqualTo("(no context available)");
    }

    // ── SaveSessionState deterministic id ─────────────────────────────────

    [Test]
    public async Task SaveSessionState_DeterministicId()
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("w:task"));
        var expected = Convert.ToHexString(hash).ToLowerInvariant()[..16];

        await _storage.UpsertDrawerAsync(expected, "w", "session", "doing task",
            "task", null, "synthesis", default);

        await using var ro = _factory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM drawers WHERE id = $id";
        cmd.Parameters.Add(new DuckDBParameter("id", expected));
        var count = (long)cmd.ExecuteScalar()!;
        await Assert.That(count).IsEqualTo(1L);
    }

    [Test]
    public async Task SaveSessionState_Idempotent_SameKeyReplaces()
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("w:session"));
        var id = Convert.ToHexString(hash).ToLowerInvariant()[..16];

        await _storage.UpsertDrawerAsync(id, "w", "session", "state v1",
            "session", null, "synthesis", default);
        await _storage.UpsertDrawerAsync(id, "w", "session", "state v2",
            "session", null, "synthesis", default);

        await using var ro = _factory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = "SELECT content FROM drawers WHERE id = $id";
        cmd.Parameters.Add(new DuckDBParameter("id", id));
        var content = (string)cmd.ExecuteScalar()!;
        await Assert.That(content).IsEqualTo("state v2");

        cmd.Parameters.Clear();
        cmd.CommandText = "SELECT count(*) FROM drawers WHERE id = $id";
        cmd.Parameters.Add(new DuckDBParameter("id", id));
        var count = (long)cmd.ExecuteScalar()!;
        await Assert.That(count).IsEqualTo(1L);
    }
}
