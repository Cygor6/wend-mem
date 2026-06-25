using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Wendmem.Models;
using Wendmem.Serialization;
using Wendmem.Services;
using Wendmem.Storage;
using Wendmem.Wiki;
using Wendmem.Wiki.Models;

namespace Wendmem.Tools;

[McpServerToolType]
sealed class DrawerTools
{
    [McpServerTool(Name = "WakeUp"), Description(
        "Get a compact map of the palace: which wiki pages exist, what facts are currently true, " +
        "and what changed recently. Always call this first when starting a session — it tells you " +
        "what knowledge is already compiled and where to look. Returns active KG facts, recent " +
        "attempt summaries (room: 'attempts'), wiki page index, and L0/L1/L2 drawer layers. " +
        "Returns titles and paths only; read full page content with WikiRead, verify cited " +
        "drawers with GetDrawer.")]
    static async Task<string> WakeUp(
        PalaceSearcher searcher,
        IEmbedder embedder,
        PalaceConfig config,
        ILogger<DrawerTools> logger,
        [Description("Wing namespace to scope the map to (e.g. 'wendmem', 'myproject'). " +
                     "Omit to use the configured default wing.")]
        string? wing = null,
        [Description("Optional topic hint that biases which wiki pages surface first. " +
                     "Pass the user's current question or task description.")]
        string? seedQuery = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (!embedder.IsAvailable)
        {
            return JsonSerializer.Serialize(
                McpResponse.Fail<WakeUpResult>("WakeUp", "internal", "Embedding model is not loaded. WakeUp requires the ONNX model."),
                WendmemJsonContext.Default.McpResponseWakeUpResult);
        }

        // Capture the caller's wing BEFORE ResolveWing forces it to DefaultWing.
        // In ForceDefaultWing deployments this value is discarded for routing but
        // still carries semantic signal; PalaceSearcher folds it into the L2 seed.
        var callerWing = wing;
        wing = PathValidator.ResolveWing(wing, config);

        logger.LogInformation("WakeUp wing={Wing} callerWing={CallerWing} seed={Seed}",
            wing, callerWing, seedQuery);
        var result = await searcher.WakeUpAsync(wing, seedQuery, ct, callerWing: callerWing);
        logger.LogInformation("WakeUp → L0:{L0} L1:{L1} L2:{L2} drawers (seedTop={Score:F2})",
            result.L0, result.L1, result.L2, result.SeedTopScore);

        // Per SKILL.md v4.2 §2, WakeUp MUST return confidence: null (only
        // SearchMemories populates confidence). The honest match signal lives in
        // decision_support, derived from the real pre-exclusion seed-match score
        // against the existing wing-resolved thresholds (WakeUpMinL2Score,
        // CanProceedMin). can_proceed stays true: the map loaded and L0/L1 are
        // valid orientation regardless of whether the seed matched.
        var (suggestedAction, summary) = ComputeWakeUpGuidance(
            seedQuery, result.SeedTopScore, result.L2, result.SeedLabels,
            config.WakeUpMinL2Score, config.GetThresholds(wing).CanProceedMin);

        var decisionSupport = new McpDecisionSupport(
            CanProceed: true,
            SuggestedAction: suggestedAction,
            Summary: summary
        );

        return JsonSerializer.Serialize(
            McpResponse.Ok("WakeUp", result, sw.ElapsedMilliseconds, decisionSupport: decisionSupport),
            WendmemJsonContext.Default.McpResponseWakeUpResult);
    }

    /// <summary>
    /// Pure four-band decision for the WakeUp envelope. Extracted so it is
    /// unit-testable without the MCP host. Bands (wing-resolved thresholds):
    /// no-seed → proceed; confident (top ≥ CanProceedMin AND ≥1 L2 survivor) →
    /// proceed; near-miss (top ≥ WakeUpMinL2Score) → ask_user; empty → verify.
    /// </summary>
    internal static (SuggestedAction Action, string Summary) ComputeWakeUpGuidance(
        string? seedQuery, float seedTopScore, int l2Survivors,
        string[]? seedLabels, float minL2, float canProceedMin)
    {
        // Locale-stable score formatting so the agent-visible summary does not
        // vary by server culture (comma vs dot decimal separator).
        var top = seedTopScore.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(seedQuery))
            return (SuggestedAction.Proceed,
                "No seedQuery provided — returning synthesis and recent drawers only; no semantic search performed.");

        if (seedTopScore >= canProceedMin && l2Survivors >= 1)
            return (SuggestedAction.Proceed,
                $"Matched {l2Survivors} drawer(s) for your query (top {top}).");

        if (seedTopScore >= minL2)
        {
            var labels = seedLabels is { Length: > 0 } l
                ? string.Join(", ", l)
                : "(none named)";
            return (SuggestedAction.AskUser,
                $"No confident match for your query. Nearest context: {labels}. Confirm what you mean before relying on this.");
        }

        return (SuggestedAction.Verify,
            $"No semantic matches for your query (top candidate {top}). Showing synthesis and recent context only — do not treat recent drawers as answers.");
    }

    [McpServerTool(Name = "SearchMemories"), Description(
        "Search drawers — the verbatim, immutable chunks of mined files and conversations — " +
        "using hybrid BM25 + semantic search. Use this for conceptual queries: how something " +
        "works, an approach, a topic, a named entity you can describe in words. " +
        "Do NOT use for an exact symbol, method name, error string, hex ID, or any token with " +
        "dots/underscores/brackets — BM25 stems identifiers and will miss them; use GrepExact instead. " +
        "For LLM-synthesized topic pages rather than raw source, use WikiSearch instead.")]
    static async Task<string> SearchMemories(
        IEmbedder embedder,
        PalaceSearcher searcher,
        PalaceConfig palaceConfig,
        DrawerStorage storage,
        KnowledgeGraph kg,
        ILogger<DrawerTools> logger,
        [Description("Search query in natural language. Distinctive nouns and exact phrases " +
                     "work better than abstract descriptions. Example: 'duckdb hnsw cosine' " +
                     "beats 'how vector search works'.")]
        string query,
        [Description("Wing to scope the search to. Highly recommended — narrows the corpus " +
                     "and improves relevance significantly.")]
        string? wing = null,
        [Description("Optional further scope below wing (e.g. 'architecture', 'decisions').")]
        string? room = null,
        [Description("Max results to return. Default 10; raise to 25 for broad surveys, " +
                     "lower to 3-5 for targeted lookups.")]
        int k = 10,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (!embedder.IsAvailable)
        {
            return JsonSerializer.Serialize(
                McpResponse.Fail<List<SearchMemoriesHit>>("SearchMemories", "internal", "Embedding model is not loaded."),
                WendmemJsonContext.Default.McpResponseListSearchMemoriesHit);
        }

        wing = PathValidator.ResolveWing(wing, palaceConfig);
        room = PathValidator.ValidateOptionalRoom(room);

        logger.LogInformation("SearchMemories wing={Wing} query={Query} k={K}", wing, query, k);
        var results = await searcher.SearchMemoriesAsync(query, wing, room, k, ct);

        var hits = results.Select(r => new SearchMemoriesHit(
                r.Drawer.Id, r.Drawer.Wing, r.Drawer.Room,
                r.Drawer.Content, r.Drawer.Source, r.Regime.ToString())).ToList();

        float bestScore = results.FirstOrDefault()?.Score ?? 0f;
        logger.LogInformation("SearchMemories → {Count} hits  best={Best:F2}", results.Count, bestScore);

        // Insufficient evidence check: when MinRetrievalScore > 0 and all scores
        // fall below the threshold, return a structured signal instead of results.
        if (palaceConfig.MinRetrievalScore > 0f &&
            results.Count > 0 &&
            results.All(r => r.Score < palaceConfig.MinRetrievalScore))
        {
            var insufficientDecisionSupport = new McpDecisionSupport(
                CanProceed: false,
                SuggestedAction: SuggestedAction.Retry,
                Summary: $"No drawers in wing '{wing ?? "*"}' matched query with sufficient confidence. " +
                         $"Consider broadening the query or mining more content.");

            return JsonSerializer.Serialize(
                McpResponse.Fail<List<SearchMemoriesHit>>(
                    "SearchMemories",
                    "insufficient_evidence",
                    insufficientDecisionSupport.Summary),
                WendmemJsonContext.Default.McpResponseListSearchMemoriesHit);
        }

        var t = palaceConfig.GetThresholds(wing);

        // Multi-signal detection for the top result
        McpSignals? signals = null;
        string agreement = "not_applicable";

        if (results.Count > 0)
        {
            var topId = results[0].Drawer.Id;

            // BM25 signal: check if the top result appeared in BM25/FTS retrieval
            var bm25Results = await storage.FtsSearchAsync(query, wing, limit: 50, ct);
            var bm25Active = bm25Results.Any(r => r.Drawer.Id == topId);

            // Semantic signal: cosine score exceeds medium threshold
            var semanticActive = bestScore > t.Medium;

            // KG entity signal: check if KG entity lookup contributed
            var kgEntities = await kg.MatchEntitiesInTextAsync(query, limit: 5, ct);
            var kgActive = kgEntities.Count > 0;

            signals = new McpSignals(bm25Active, bestScore, kgActive);

            int activeCount = (bm25Active ? 1 : 0) + (semanticActive ? 1 : 0) + (kgActive ? 1 : 0);
            agreement = activeCount switch
            {
                3 => "full",
                2 => "partial",
                1 => "single",
                _ => "not_applicable"
            };
        }

        var confidence = new McpConfidence(
            Level: bestScore > t.High ? "high" : bestScore > t.Medium ? "medium" : "low",
            Score: bestScore,
            Reason: bestScore > t.High ? "exact_match" : bestScore > t.CanProceedMin ? "semantic_match" : "poor_match",
            Signals: signals,
            Agreement: agreement
        );

        var decisionSupport = new McpDecisionSupport(
            CanProceed: hits.Count > 0 && bestScore > t.CanProceedMin,
            SuggestedAction: hits.Count > 0 && bestScore > t.CanProceedMin ? SuggestedAction.Proceed : (hits.Count > 0 ? SuggestedAction.Verify : SuggestedAction.Retry),
            Summary: hits.Count > 0
                ? $"Found {hits.Count} memories. Best match score: {bestScore:F2}."
                : "No relevant memories found. Try using more specific nouns."
        );

        return JsonSerializer.Serialize(
            McpResponse.Ok("SearchMemories", hits, sw.ElapsedMilliseconds, confidence, decisionSupport),
            WendmemJsonContext.Default.McpResponseListSearchMemoriesHit);
    }

    [McpServerTool(Name = "GetDrawer"), Description(
        "Read the full verbatim content of one specific drawer by its 16-character ID. " +
        "Use this to verify a citation cited on a wiki page, or to deep-read a drawer surfaced " +
        "by SearchMemories when the search snippet isn't enough. Drawers are immutable — " +
        "this is read-only by design.")]
    static async Task<string> GetDrawer(
        DrawerStorage storage,
        ILogger<DrawerTools> logger,
        [Description("Drawer ID: exactly 16 lowercase hex characters. " +
                     "Example: 'a3f2b1c8d4e5f607'. IDs come from WakeUp's citations or " +
                     "SearchMemories results.")]
        string id,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        id = PathValidator.ValidateDrawerId(id);

        logger.LogInformation("GetDrawer id={Id}", id);
        var drawer = await storage.GetByIdAsync(id, ct);
        if (drawer is null)
        {
            return JsonSerializer.Serialize(
                McpResponse.Fail<GetDrawerDto>("GetDrawer", "not_found",
                    $"Drawer '{id}' not found. Drawer IDs are 16 hex chars, obtained from " +
                    $"SearchMemories results, GrepExact results, or wiki page citations. " +
                    $"To find drawers by topic, use SearchMemories; for an exact term, use GrepExact."),
                WendmemJsonContext.Default.McpResponseGetDrawerDto);
        }

        logger.LogInformation("GetDrawer → drawer {Id}", drawer.Id);
        var dto = new GetDrawerDto(
            drawer.Id, drawer.Wing, drawer.Room, drawer.Content,
            drawer.Source,
            drawer.MinedAt.ToUnixTimeSeconds());

        var decisionSupport = new McpDecisionSupport(
            CanProceed: true,
            SuggestedAction: SuggestedAction.Proceed,
            Summary: $"Retrieved drawer {id} from {drawer.Wing}/{drawer.Room}."
        );

        return JsonSerializer.Serialize(
            McpResponse.Ok("GetDrawer", dto, sw.ElapsedMilliseconds, decisionSupport: decisionSupport),
            WendmemJsonContext.Default.McpResponseGetDrawerDto);
    }

    [McpServerTool(Name = "GrepExact"), Description(
        "Exact string or regex search over raw drawer content (DuckDB RE2 syntax). " +
        "Use when you know the precise term to find: a symbol name, method, error message, " +
        "hex ID, version string, SQL fragment, or any exact phrase. Faster and more precise " +
        "than SearchMemories for evidence-location tasks, and returns source file paths so you " +
        "can refine further. Do NOT use for a vague concept or question — exact matching returns " +
        "nothing for fuzzy queries; use SearchMemories instead.")]
    public static async Task<string> GrepExact(
        DrawerStorage storage,
        PalaceConfig config,
        [Description(
            "Exact string or regex pattern to match (DuckDB RE2 syntax). " +
            "Examples: 'DrawerStorage\\.MmrRerank', 'ON CONFLICT DO NOTHING', " +
            "'v1\\.5\\.\\d+', 'a3f2b1c8d4e5f607'. " +
            "Use '(?i)' prefix for case-insensitive match.")]
        string pattern,
        [Description("Wing to scope the search to. Strongly recommended — unscoped regex " +
                     "over a large palace is slow and unfocused. Example: 'wendmem'.")]
        string? wing = null,
        [Description("Optional room to scope below wing. Example: 'code', 'config', 'docs'.")]
        string? room = null,
        [Description("Max results to return. Default 20; lower to 5 for a precise lookup.")]
        int k = 20,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        wing = PathValidator.ResolveWing(wing, config);
        room = PathValidator.ValidateOptionalRoom(room);
        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern);
        }
        catch (ArgumentException ex)
        {
            return JsonSerializer.Serialize(
                McpResponse.Fail<IReadOnlyList<GrepExactResult>>("GrepExact", "invalid_input",
                    $"Invalid regex: {ex.Message}. Use RE2 syntax — no backreferences (\\1) or " +
                    $"lookaheads ((?=...)). Prefix '(?i)' for case-insensitive matching."),
                WendmemJsonContext.Default.McpResponseIReadOnlyListGrepExactResult);
        }

        var results = await storage.GrepExactAsync(pattern, wing, room, k, ct);
        var resList = (IReadOnlyList<GrepExactResult>)results;

        var decisionSupport = new McpDecisionSupport(
            CanProceed: resList.Count > 0,
            SuggestedAction: resList.Count > 0 ? SuggestedAction.Proceed : SuggestedAction.Retry,
            Summary: resList.Count > 0
                ? $"Found {resList.Count} exact matches for '{pattern}'."
                : $"No exact matches found for '{pattern}'."
        );

        return JsonSerializer.Serialize(
            McpResponse.Ok("GrepExact", resList, sw.ElapsedMilliseconds, decisionSupport: decisionSupport),
            WendmemJsonContext.Default.McpResponseIReadOnlyListGrepExactResult);
    }

    [McpServerTool(Name = "AddMemory"), Description(
        "Store text as a new drawer — verbatim, immutable, deduplicated by content hash. " +
        "Use when the user shares context worth preserving exactly: a decision, an error message, " +
        "a code snippet, a key fact. Idempotent on identical content. " +
        "For derived synthesis you produce yourself from multiple drawers, use WikiWrite instead.")]
    static async Task<string> AddMemory(
        DrawerStorage storage,
        HallDetector hallDetector,
        WalLogger wal,
        PalaceConfig config,
        ILogger<DrawerTools> logger,
        [Description("Verbatim content to store. Do NOT paraphrase or summarize — the value of " +
                     "a drawer is that it preserves the original wording. If you need to compress, " +
                     "use WikiWrite, not AddMemory.")]
        string text,
        [Description("Wing namespace (optional — omit to use the configured default wing). " +
                     "Example: 'wendmem', 'user-prefs'.")]
        string? wing = null,
        [Description("Subdivision within the wing (e.g. 'architecture', 'decisions', 'bugs', 'meetings').")]
        string? room = null,
        [Description("Optional origin (file path, URL, or conversation URI) for traceability. " +
                     "Helpful when reviewing later where a drawer came from.")]
        string? source = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        wing = PathValidator.ResolveWing(wing, config);
        room = PathValidator.ValidateOptionalRoom(room);

        room ??= hallDetector.Detect(text.AsSpan());

        logger.LogInformation("AddMemory wing={Wing} room={Room}", wing, room);

        wal.Log("add_drawer", new Dictionary<string, string?>
        {
            ["wing"] = wing,
            ["room"] = room,
            ["content_len"] = text.Length.ToString(),
            ["content_preview"] = text.Length > 100 ? text[..100] + "..." : text
        });

        var result = await storage.AddDrawerAsync(text, wing, room, source, null, drawerType: "source", ct);
        var id = result.Id;

        logger.LogInformation("AddMemory → drawer {Id}", id);

        var dto = new AddMemoryResult(id, wing, room, result.Admitted, result.Reason, result.MatchedId);

        var decisionSupport = new McpDecisionSupport(
            CanProceed: result.Admitted,
            SuggestedAction: result.Admitted ? SuggestedAction.Proceed : SuggestedAction.Verify,
            Summary: result.Admitted
                ? $"Memory stored with ID {id} in {wing}/{room}."
                : $"Memory rejected: {result.Reason}. It matched existing drawer {result.MatchedId}."
        );

        return JsonSerializer.Serialize(
            McpResponse.Ok("AddMemory", dto, sw.ElapsedMilliseconds, decisionSupport: decisionSupport),
            WendmemJsonContext.Default.McpResponseAddMemoryResult);
    }
}
