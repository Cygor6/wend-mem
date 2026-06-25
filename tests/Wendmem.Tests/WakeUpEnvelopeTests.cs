using DuckDB.NET.Data;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Wendmem.Experiences;
using Wendmem.Models;
using Wendmem.Serialization;
using Wendmem.Services;
using Wendmem.Storage;
using Wendmem.Tools;
using Wendmem.Wiki;

namespace Wendmem.Tests;

/// <summary>
/// Smoke tests for the WakeUp envelope honesty fix (SKILL.md v4.2 §2 conformance).
///
/// The live corpus referenced by the original bug ("Autofac ...", "invoice auto pick
/// rule ...") is not present in this environment, so these tests use:
///   - the pure four-band helper (ComputeWakeUpGuidance) for deterministic band coverage,
///   - the real McpResponse serialization path for the contract (confidence: null),
///   - a controlled temp corpus + keyword embedder for the pre-exclusion seed-score capture.
/// </summary>
public class WakeUpEnvelopeTests
{
    const float MinL2 = 0.25f;
    const float CanProceedMin = 0.40f;

    // ── Pure four-band logic ───────────────────────────────────────────────

    /// <summary>Smoke #1 / empty band: zero-match seed → verify, no "proceed".</summary>
    [Test]
    public async Task Band_EmptyMatch_Yields_Verify()
    {
        var (action, summary) = DrawerTools.ComputeWakeUpGuidance(
            "Autofac package version dependency NoEffect build errors",
            seedTopScore: 0.10f, l2Survivors: 0,
            seedLabels: null, minL2: MinL2, canProceedMin: CanProceedMin);

        await Assert.That(action).IsEqualTo(SuggestedAction.Verify);
        await Assert.That(summary).Contains("No semantic matches");
        await Assert.That(summary).Contains("do not treat recent drawers as answers");
        Console.Out.WriteLine($"[empty] action={action} summary={summary}");
    }

    /// <summary>Smoke #2 / near-miss band (0.25–0.40) → ask_user, names near-misses.</summary>
    [Test]
    public async Task Band_NearMiss_Yields_AskUser()
    {
        var (action, summary) = DrawerTools.ComputeWakeUpGuidance(
            "partial topic phrasing",
            seedTopScore: 0.30f, l2Survivors: 0,
            seedLabels: new[] { "invoice-rules.md", "pick-service.cs", "config.json" },
            minL2: MinL2, canProceedMin: CanProceedMin);

        await Assert.That(action).IsEqualTo(SuggestedAction.AskUser);
        await Assert.That(summary).Contains("No confident match");
        await Assert.That(summary).Contains("invoice-rules.md");
        await Assert.That(summary).Contains("Confirm what you mean");
        Console.Out.WriteLine($"[near-miss] action={action} summary={summary}");
    }

    /// <summary>Smoke #3 / confident band (≥0.40 AND ≥1 L2 survivor) → proceed + real score.</summary>
    [Test]
    public async Task Band_Confident_Yields_Proceed()
    {
        var (action, summary) = DrawerTools.ComputeWakeUpGuidance(
            "invoice auto pick rule RULE_TYPE",
            seedTopScore: 0.72f, l2Survivors: 3,
            seedLabels: new[] { "pick-service.cs" },
            minL2: MinL2, canProceedMin: CanProceedMin);

        await Assert.That(action).IsEqualTo(SuggestedAction.Proceed);
        await Assert.That(summary).Contains("Matched 3 drawer(s)");
        await Assert.That(summary).Contains("0.72");
        Console.Out.WriteLine($"[confident] action={action} summary={summary}");
    }

    /// <summary>Smoke #4 / no-seed band → proceed, no semantic search performed.</summary>
    [Test]
    public async Task Band_NoSeed_Yields_Proceed_NoSearch()
    {
        var (action, summary) = DrawerTools.ComputeWakeUpGuidance(
            seedQuery: null,
            seedTopScore: 0f, l2Survivors: 0,
            seedLabels: null, minL2: MinL2, canProceedMin: CanProceedMin);

        await Assert.That(action).IsEqualTo(SuggestedAction.Proceed);
        await Assert.That(summary).Contains("No seedQuery provided");
        await Assert.That(summary).Contains("no semantic search performed");
        Console.Out.WriteLine($"[no-seed] action={action} summary={summary}");
    }

    /// <summary>
    /// A high score alone is NOT enough for "proceed" — there must be ≥1 L2 survivor.
    /// Guards the de-dup edge: a 1.0 match that was excluded into L1 must not claim
    /// confident "proceed".
    /// </summary>
    [Test]
    public async Task Band_HighScoreButNoL2Survivor_FallsTo_AskUser()
    {
        var (action, summary) = DrawerTools.ComputeWakeUpGuidance(
            "exact topic", seedTopScore: 1.0f, l2Survivors: 0,
            seedLabels: new[] { "topic.md" },
            minL2: MinL2, canProceedMin: CanProceedMin);

        await Assert.That(action).IsEqualTo(SuggestedAction.AskUser);
        Console.Out.WriteLine($"[dedup-edge] action={action} summary={summary}");
    }

    // ── Contract: confidence must be null + no deterministic block ─────────

    /// <summary>Smoke #5 / contract: WakeUp envelope carries confidence: null and
    /// never the high/1.0/deterministic block (which previously violated §2).</summary>
    [Test]
    public async Task Contract_WakeUpEnvelope_ConfidenceIsNull_NoDeterministic()
    {
        var result = new WakeUpResult("body", 1, 2, 0, 0.10f, null);
        var decision = new McpDecisionSupport(true, SuggestedAction.Verify, "no match");

        // WakeUp calls Ok(...) WITHOUT a confidence argument — the regression path.
        var resp = McpResponse.Ok("WakeUp", result, 5, decisionSupport: decision);

        await Assert.That(resp.Confidence).IsNull();

        var json = JsonSerializer.Serialize(resp, WendmemJsonContext.Default.McpResponseWakeUpResult);
        Console.Out.WriteLine($"[contract] envelope json={json}");

        await Assert.That(json.Contains("\"confidence\"")).IsFalse()
            .Because("confidence must be omitted/null for non-SearchMemories tools");
        await Assert.That(json.Contains("deterministic")).IsFalse();
        await Assert.That(json.Contains("\"high\"")).IsFalse();
    }

    // ── Pre-exclusion seed-score capture (Phase 2 honesty) ─────────────────

    /// <summary>
    /// A drawer that is BOTH the top semantic match AND recent is de-duped into L1
    /// (excludeIds), so L2 survivors = 0. But SeedTopScore must still reflect the
    /// real pre-exclusion top score — the honest "did anything match" signal that
    /// the original bug suppressed (it reported confidence high on l2:0).
    /// </summary>
    DuckDbConnectionFactory _factory = null!;
    string _dbPath = null!;

    /// <summary>Unit-vector embedder keyed by content markers (dim 512, matching schema).</summary>
    sealed class KeywordEmbedder : IEmbedder
    {
        public bool IsAvailable => true;
        public int EmbeddingDimension => 512;

        static float[] Vec(string text)
        {
            var v = new float[512];
            if (text.Contains("alpha"))
                v[0] = 1f;        // cosine(alpha,alpha)=1
            else if (text.Contains("bravo"))
                v[1] = 1f;    // orthogonal to alpha
            else
                v[511] = 1f;                               // orthogonal to query axes
            return v;
        }

        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default) => new(Vec(text));
        public ValueTask<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default) => new(Vec(text));
        public ValueTask<float[]> EmbedQueryAsync(string text, CancellationToken ct = default) => new(Vec(text));
    }

    /// <summary>
    /// Embedder that records the last text passed to EmbedQueryAsync, so tests can
    /// assert what WakeUp actually fed to the L2 embedding (the effective seed).
    /// Returns a fixed unit vector so storage indexing succeeds.
    /// </summary>
    sealed class RecordingEmbedder : IEmbedder
    {
        public bool IsAvailable => true;
        public int EmbeddingDimension => 512;
        public string? LastQuery { get; private set; }

        static float[] Vec() { var v = new float[512]; v[0] = 1f; return v; }

        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default) => new(Vec());
        public ValueTask<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default) => new(Vec());
        public ValueTask<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)
        { LastQuery = text; return new(Vec()); }
    }

    [Before(Test)]
    public void Init()
    {
        _dbPath = Path.GetTempFileName() + ".duckdb";
        _factory = new DuckDbConnectionFactory(_dbPath);
    }

    [After(Test)]
    public async Task Dispose()
    {
        await _factory.DisposeAsync();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    async Task<(DrawerStorage storage, PalaceSearcher searcher)> BuildAsync(
        IEmbedder embedder, PalaceConfig? config = null)
    {
        config ??= new PalaceConfig { AdmissionEnabled = false };
        var closets = new ClosetStorage(_factory);
        var aaak = new AaakDialect();
        var entityIndex = new EntityIndexService(_factory);
        var kg = new KnowledgeGraph(_factory);
        var storage = new DrawerStorage(_factory, embedder, closets, aaak, entityIndex, kg,
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 500 }), config);
        var loggerFactory = LoggerFactory.Create(b => { });
        var wiki = new WikiStorage(_factory, embedder,
            loggerFactory.CreateLogger<WikiStorage>(),
            new MemoryCache(new MemoryCacheOptions()));
        var llm = new LlmService(new FakeChatClient(), "fake");
        var searcher = new PalaceSearcher(storage, embedder, kg, wiki, entityIndex,
            config, new MemoryCache(new MemoryCacheOptions { SizeLimit = 500 }),
            llm, loggerFactory.CreateLogger<PalaceSearcher>(), null, null);
        return (storage, searcher);
    }

    sealed class FakeChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "[]")));
        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Test]
    public async Task Capture_SeedTopScore_PreExclusion_WhenMatchIsDedupedIntoL1()
    {
        var embedder = new KeywordEmbedder();
        var (storage, searcher) = await BuildAsync(embedder);

        // A source drawer that exactly matches the seed semantically (cosine ≈ 1.0).
        // Because it is freshly added it is also the most-recent source drawer, so
        // WakeUp de-dups it into L1 (excludeIds) and it cannot appear in L2.
        await storage.AddDrawerAsync("alpha secret details", "w", "source",
            source: "alpha.md", sourceMtime: null, drawerType: "source", ct: default);

        var result = await searcher.WakeUpAsync("w", "alpha", default);

        Console.Out.WriteLine($"[capture] SeedTopScore={result.SeedTopScore:F4} L0={result.L0} L1={result.L1} L2={result.L2} labels=[{string.Join(",", (IEnumerable<string>?)result.SeedLabels ?? Array.Empty<string>())}]");

        await Assert.That(result.SeedTopScore).IsGreaterThanOrEqualTo(0.99f)
            .Because("the real pre-exclusion top candidate scored ~1.0; the envelope must report it honestly");
        await Assert.That(result.L2).IsEqualTo(0)
            .Because("the match was de-duped into L1 as a recent drawer");
        await Assert.That(result.L1).IsGreaterThanOrEqualTo(1)
            .Because("the matched drawer still ships as recent content (L0/L1 untouched)");

        // And the honest band for this state is ask_user (1.0 score but no L2 survivor),
        // NOT the old fabricated high-confidence "proceed".
        var (action, _) = DrawerTools.ComputeWakeUpGuidance(
            "alpha", result.SeedTopScore, result.L2, result.SeedLabels, MinL2, CanProceedMin);
        await Assert.That(action).IsEqualTo(SuggestedAction.AskUser);
    }

    // ── ForceDefaultWing: caller wing as a retrieval hint ───────────────────

    /// <summary>
    /// ForceDefaultWing=true + a caller wing distinct from the default: the wing
    /// is folded into the L2 seed, so the embedder receives "&lt;wing&gt; &lt;seed&gt;".
    /// This is the core of the fix — the wing name carries semantic signal that
    /// ResolveWing otherwise discards.
    /// </summary>
    [Test]
    public async Task ForceDefaultWing_FoldsCallerWing_IntoSeed()
    {
        var embedder = new RecordingEmbedder();
        var config = new PalaceConfig
        {
            AdmissionEnabled = false,
            ForceDefaultWing = true,
            DefaultWing = "work"
        };
        var (_, searcher) = await BuildAsync(embedder, config);

        await searcher.WakeUpAsync("work", "build errors", default,
            callerWing: "autofac-troubleshooting");

        Console.Out.WriteLine($"[force-fold] embedder received: {embedder.LastQuery}");
        await Assert.That(embedder.LastQuery).IsEqualTo("autofac-troubleshooting build errors");
    }

    /// <summary>
    /// ForceDefaultWing=true but the caller wing equals the default wing: no
    /// folding (it would add noise, not signal).
    /// </summary>
    [Test]
    public async Task ForceDefaultWing_DoesNotFold_WhenCallerWingIsDefault()
    {
        var embedder = new RecordingEmbedder();
        var config = new PalaceConfig
        {
            AdmissionEnabled = false,
            ForceDefaultWing = true,
            DefaultWing = "work"
        };
        var (_, searcher) = await BuildAsync(embedder, config);

        await searcher.WakeUpAsync("work", "build errors", default, callerWing: "work");

        Console.Out.WriteLine($"[force-default] embedder received: {embedder.LastQuery}");
        await Assert.That(embedder.LastQuery).IsEqualTo("build errors");
    }

    /// <summary>
    /// Regression guard: in multi-wing mode (ForceDefaultWing=false) the wing is
    /// already a storage routing key. The seed must NOT be folded — concatenating
    /// the wing would inject noise. WakeUp output stays byte-identical to before.
    /// </summary>
    [Test]
    public async Task MultiWing_DoesNotFoldCallerWing_IntoSeed()
    {
        var embedder = new RecordingEmbedder();
        var config = new PalaceConfig
        {
            AdmissionEnabled = false,
            ForceDefaultWing = false
        };
        var (_, searcher) = await BuildAsync(embedder, config);

        await searcher.WakeUpAsync("autofac-troubleshooting", "build errors", default,
            callerWing: "autofac-troubleshooting");

        Console.Out.WriteLine($"[multiwing] embedder received: {embedder.LastQuery}");
        await Assert.That(embedder.LastQuery).IsEqualTo("build errors");
    }
}
