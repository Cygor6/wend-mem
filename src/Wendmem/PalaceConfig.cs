namespace Wendmem;

/// <summary>
/// Confidence score thresholds for SearchMemories.
/// </summary>
public sealed record ConfidenceThresholds(
    float High = 0.80f,
    float Medium = 0.60f,
    float CanProceedMin = 0.40f);

/// <summary>
/// Per-wing confidence configuration.
/// </summary>
public sealed record ConfidenceConfig(
    ConfidenceThresholds Thresholds)
{
    public ConfidenceConfig() : this(new ConfidenceThresholds()) { }
}

/// <summary>
/// Tuning knobs for the memory-palace search pipeline.
/// Bound from appsettings.json "Palace" section.
/// </summary>
public sealed class PalaceConfig
{
    /// <summary>
    /// Wing used when a tool call omits the wing parameter.
    /// Set via appsettings.json Palace:DefaultWing or env var Palace__DefaultWing.
    /// </summary>
    public string DefaultWing { get; set; } = "work";

    /// <summary>
    /// When true, all tool calls are forced to use <see cref="DefaultWing"/>
    /// regardless of the wing value passed by the caller.
    /// Useful for single-wing deployments (e.g. a personal palace).
    /// </summary>
    public bool ForceDefaultWing { get; set; } = false;

    /// <summary>
    /// Global confidence configuration with default thresholds.
    /// </summary>
    public ConfidenceConfig Confidence { get; set; } = new();

    /// <summary>
    /// Per-wing overrides for confidence thresholds. Key = wing name.
    /// </summary>
    public Dictionary<string, ConfidenceConfig> WingOverrides { get; set; } = [];

    /// <summary>
    /// Resolves confidence thresholds for a given wing.
    /// Resolution order: wing-specific override → global → hardcoded defaults.
    /// </summary>
    public ConfidenceThresholds GetThresholds(string? wing)
    {
        if (wing is not null &&
            WingOverrides.TryGetValue(wing, out var wingConfig) &&
            wingConfig.Thresholds is not null)
        {
            return wingConfig.Thresholds;
        }

        return Confidence.Thresholds ?? new ConfidenceThresholds();
    }

    /// <summary>
    /// Lambda for MMR re-ranking (0 = max diversity, 1 = max relevance).
    /// </summary>
    public float MmrLambda { get; set; } = 0.5f;

    /// <summary>
    /// Enable admission control on AddDrawerAsync.
    /// When true, near-duplicate content is rejected before embedding + indexing.
    /// </summary>
    public bool AdmissionEnabled { get; set; } = true;

    /// <summary>
    /// Cosine similarity threshold for duplicate detection.
    /// New content whose embedding exceeds this similarity to any existing
    /// representative drawer in the same wing is rejected at admission time.
    /// Set to 1.0 to effectively disable even when AdmissionEnabled is true.
    /// </summary>
    public float AdmissionDuplicateThreshold { get; set; } = 0.97f;

    /// <summary>
    /// Conflict governance mode.
    /// "off" — no governance, pure score ordering.
    /// "balanced" — ensure at least one result per cluster when k allows it.
    /// "aggressive" — force equal slots per cluster, even at score cost.
    /// </summary>
    public string ConflictGovernance { get; set; } = "balanced";

    /// <summary>
    /// Recency half-life in days. Controls how quickly the
    /// recency boost decays for recently-accessed drawers.
    /// </summary>
    public float RecencyHalfLifeDays { get; set; } = 14f;

    /// <summary>
    /// Days after which an unaccessed drawer begins accumulating a
    /// staleness decay penalty in search results.
    /// </summary>
    public float DecayStaleDays { get; set; } = 30f;

    /// <summary>
    /// Maximum score penalty applied to the stalest unaccessed drawers.
    /// </summary>
    public float DecayMaxPenalty { get; set; } = 0.2f;

    /// <summary>
    /// Drawers with access_count above this threshold are protected
    /// from pruning even when their cluster is tight.
    /// </summary>
    public int PruneAccessProtectionThreshold { get; set; } = 3;

    /// <summary>
    /// Enable topic-shift chunking (true) or use fixed-window
    /// chunking only (false).
    /// </summary>
    public bool TopicShiftChunkingEnabled { get; set; } = true;

    /// <summary>
    /// Minimum cosine distance between consecutive chunks
    /// to count as a topic shift. Lower = more splits, higher = fewer.
    /// </summary>
    public float TopicShiftThreshold { get; set; } = 0.60f;



    /// <summary>
    /// Target chunk size in tokens. Chunks aim to be
    /// this many tokens, within [MinChunkTokens, MaxChunkTokens].
    /// Default 512 fits comfortably within EmbeddingGemma-300M's 2048 context.
    /// </summary>
    public int TargetChunkTokens { get; set; } = 800;

    /// <summary>
    /// Minimum chunk size in tokens.
    /// </summary>
    public int MinChunkTokens { get; set; } = 80;

    /// <summary>
    /// Maximum chunk size in tokens.
    /// </summary>
    public int MaxChunkTokens { get; set; } = 1800;

    /// <summary>
    /// When > 0, prepend the last sentence of the previous
    /// chunk to each chunk (except the first), up to this many characters.
    /// Default 0 = no overlap. When > 0, prepend the last N sentences of the previous chunk.
    /// but this is available as an opt-in for retrieval robustness.
    /// </summary>
    public int ChunkOverlapSentences { get; set; } = 0;

    /// <summary>
    /// When true, SearchMemories generates alternative query phrasings via LLM
    /// and runs parallel HybridSearch calls, merging results by highest score.
    /// </summary>
    public bool EnableQueryExpansion { get; set; } = false;

    /// <summary>
    /// Number of alternative phrasings to generate when EnableQueryExpansion is true.
    /// The original query always runs; this controls how many extra variants are added.
    /// </summary>
    public int QueryExpansionVariants { get; set; } = 2;

    /// <summary>
    /// Salience weight for importance-based ranking boost. 0 = ignore importance,
    /// higher = more weight on salience. Default 0.10 — salience is a tiebreaker,
    /// never let it dominate. Applied in HybridSearchAsync after RRF fusion
    /// and side-index boost, before MMR.
    /// </summary>
    public float SalienceWeight { get; set; } = 0.10f;

    /// <summary>
    /// Root directory for SKILL.md folders. Skills are discovered on disk and
    /// indexed into DuckDB for retrieval. Tilde (~) expands to user home.
    /// Default: ~/.wendmem/skills
    /// </summary>
    public string SkillsRoot { get; set; } = "";

    /// <summary>
    /// Whether to watch SkillsRoot for filesystem changes and auto-reindex.
    /// Default false — use `wendmem skills reindex` CLI for manual sync.
    /// </summary>
    public bool WatchSkillsRoot { get; set; } = false;

    /// <summary>
    /// Minimum score threshold for SearchMemories results. When set to a value
    /// greater than 0.0, if ALL returned drawers have scores below this threshold,
    /// the response includes status "insufficient_evidence" with an empty results
    /// list and a message suggesting the user broaden the query. Default 0.0
    /// disables the check entirely — all results are returned regardless of score.
    /// </summary>
    public float MinRetrievalScore { get; set; } = 0.0f;

    /// <summary>
    /// Minimum cosine similarity score for WakeUp L2 (semantic) drawers.
    /// Drawers with raw cosine similarity below this threshold are excluded from
    /// WakeUp context even if they are within top-k. This prevents semantically-close-but-
    /// irrelevant hard distractors from entering the prompt.
    /// Default 0.25 filters clear noise while keeping genuinely relevant results.
    /// Set to 0.0 to disable filtering (include all top-k results).
    /// </summary>
    public float WakeUpMinL2Score { get; set; } = 0.25f;

    /// <summary>
    /// Hard character budget for WakeUp output. The total character count of the
    /// WakeUp response (including header, L0, L1, L2, and tail sections) will not
    /// exceed this value. When truncation is needed, layers are prioritized:
    /// L0 (synthesis) is truncated first, then L2 (semantic), then L1 (recent).
    /// L1 is never truncated below 1 result.
    /// Default 3200 matches the prior hardcoded budget.
    /// </summary>
    public int WakeUpCharBudget { get; set; } = 3200;
}
