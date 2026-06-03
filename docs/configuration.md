# Wendmem Configuration Reference

All settings live in `appsettings.json` and can be overridden via environment variables using double-underscore syntax (e.g. `$env:Palace__DefaultWing = "personal"`).

---

## Palace — Memory & Search Pipeline

The `Palace` section controls retrieval, ranking, admission, chunking, and WakeUp behaviour.

### Wing Routing

| Key | Type | Default | Description |
|---|---|---|---|
| `DefaultWing` | `string` | `"work"` | Wing used when a tool call omits the `wing` parameter. Any kebab-case ASCII value (e.g. `"personal"`, `"my-project"`). |
| `ForceDefaultWing` | `bool` | `false` | When `true`, **all** tool calls are forced to use `DefaultWing` regardless of what the caller passes. Useful for single-wing deployments — agents can pass any wing but the server ignores it. |

### Confidence Thresholds

| Key | Type | Default | Description |
|---|---|---|---|
| `Confidence:Thresholds:High` | `float` | `0.80` | Score above which SearchMemories reports `level: "high"`. |
| `Confidence:Thresholds:Medium` | `float` | `0.60` | Score above which SearchMemories reports `level: "medium"`. |
| `Confidence:Thresholds:CanProceedMin` | `float` | `0.40` | Minimum score for `can_proceed: true`. Below this, the agent should verify or broaden the query. |
| `WingOverrides` | `object` | `{}` | Per-wing confidence overrides. Key = wing name, value = `{ Thresholds: { High, Medium, CanProceedMin } }`. Resolution: wing override → global → hardcoded defaults. |

**Example — stricter thresholds for a high-stakes wing:**

```json
"Palace": {
  "WingOverrides": {
    "production": {
      "Thresholds": { "High": 0.90, "Medium": 0.70, "CanProceedMin": 0.50 }
    }
  }
}
```

### Admission Control (Duplicate Prevention)

| Key | Type | Default | Description |
|---|---|---|---|
| `AdmissionEnabled` | `bool` | `true` | When `true`, `AddMemory` checks cosine similarity against existing drawers before indexing. Near-duplicates are rejected. |
| `AdmissionDuplicateThreshold` | `float` | `0.97` | Cosine similarity threshold for duplicate rejection. Set to `1.0` to effectively disable even when `AdmissionEnabled` is `true`. |

### Conflict Governance (Result Diversity)

| Key | Type | Default | Description |
|---|---|---|---|
| `ConflictGovernance` | `string` | `"balanced"` | How SearchMemories ensures result diversity across clusters. `"off"` = pure score ordering. `"balanced"` = at least one result per cluster when k allows. `"aggressive"` = force equal slots per cluster, even at score cost. |

### Recency & Decay

| Key | Type | Default | Description |
|---|---|---|---|
| `RecencyHalfLifeDays` | `float` | `14` | How quickly the recency boost decays. Newly-accessed drawers get a boost that halves every N days. Lower = faster decay. |
| `DecayStaleDays` | `float` | `30` | Days after which an unaccessed drawer starts accumulating a staleness penalty. |
| `DecayMaxPenalty` | `float` | `0.20` | Maximum score penalty (0–1) applied to the stalest drawers. |
| `PruneAccessProtectionThreshold` | `int` | `3` | Drawers accessed more than this many times are protected from pruning even when their cluster is tight. |

### MMR Re-Ranking

| Key | Type | Default | Description |
|---|---|---|---|
| `MmrLambda` | `float` | `0.5` | Trade-off between relevance and diversity in MMR re-ranking. `0` = maximum diversity, `1` = maximum relevance. |

### Chunking (Source Drawer Ingestion)

These control how source files are split into drawers at mining time.

| Key | Type | Default | Description |
|---|---|---|---|
| `TopicShiftChunkingEnabled` | `bool` | `true` | When `true`, chunks are split at semantic topic boundaries. When `false`, fixed-window chunking is used. |
| `TopicShiftThreshold` | `float` | `0.60` | Minimum cosine distance between consecutive chunks to count as a topic shift. Lower = more splits, higher = fewer. |
| `TargetChunkTokens` | `int` | `800` | Target chunk size in tokens. Chunks aim for this within `[MinChunkTokens, MaxChunkTokens]`. |
| `MinChunkTokens` | `int` | `80` | Minimum chunk size. Shorter segments are merged with neighbours. |
| `MaxChunkTokens` | `int` | `1800` | Maximum chunk size. Longer segments are force-split. |
| `ChunkOverlapSentences` | `int` | `0` | When > 0, prepend the last N sentences of the previous chunk to each chunk (except the first) for overlap. `0` = no overlap. |

### Query Expansion

| Key | Type | Default | Description |
|---|---|---|---|
| `EnableQueryExpansion` | `bool` | `false` | When `true`, SearchMemories generates alternative query phrasings via LLM and merges results. Increases latency but improves recall on ambiguous queries. |
| `QueryExpansionVariants` | `int` | `2` | Number of alternative phrasings to generate (the original query always runs too). |

### Salience (Importance Weighting)

| Key | Type | Default | Description |
|---|---|---|---|
| `SalienceWeight` | `float` | `0.10` | Weight of the importance-based ranking boost. `0` = ignore importance entirely. Higher = more weight on salience. Applied after RRF fusion, before MMR. |

### WakeUp Tuning

| Key | Type | Default | Description |
|---|---|---|---|
| `WakeUpMinL2Score` | `float` | `0.25` | Minimum cosine similarity for WakeUp L2 (semantic) drawers. Results below this are excluded. Set to `0.0` to include all top-k results. |
| `WakeUpCharBudget` | `int` | `3200` | Hard character limit on the WakeUp response. When truncation is needed: L0 (synthesis) is cut first, then L2 (semantic), then L1 (recent). L1 always keeps at least 1 result. |
| `MinRetrievalScore` | `float` | `0.0` | Global minimum score for SearchMemories results. When > 0, if ALL results are below this threshold, the response returns `insufficient_evidence` with an empty list. |

### Skills

| Key | Type | Default | Description |
|---|---|---|---|
| `SkillsRoot` | `string` | `""` | Root directory for `SKILL.md` folders. Tilde (`~`) expands to user home. Default empty = `~/.wendmem/skills`. |
| `WatchSkillsRoot` | `bool` | `false` | When `true`, watches `SkillsRoot` for filesystem changes and auto-reindexes. `false` = use `wendmem skills reindex` CLI for manual sync. |

---

## Experiences — Episode Learning Pipeline

The `Experiences` section controls how `RecordEpisode`, `FindEpisodes`, and the experience refinement pipeline behave.

| Key | Type | Default | Description |
|---|---|---|---|
| `TopK` | `int` | `5` | Number of candidate memories to retrieve during reflection. |
| `SamplingN` | `int` | `8` | Number of trajectory segments sampled for extraction. |
| `MaxReflectionAttempts` | `int` | `3` | Maximum retry attempts during LLM-based reflection. |
| `PruneMinRetrievals` | `int` | `5` | Minimum retrieval count before a task memory is eligible for pruning. Memories retrieved fewer times are considered low-value. |
| `PruneUtilityThreshold` | `float` | `0.5` | Utility score threshold for pruning. Memories below this after `PruneMinRetrievals` retrievals are candidates for removal. |
| `ValidationMinScore` | `float` | `0.5` | Minimum similarity score for a validated experience memory. |
| `SuccessScoreThreshold` | `float` | `1.0` | Score threshold above which a memory is considered a confirmed success. |
| `DedupSimilarityThreshold` | `float` | `0.92` | Cosine similarity threshold for experience deduplication. New memories above this similarity to existing ones are merged rather than duplicated. |
| `EnableSoftComparison` | `bool` | `true` | When `true`, uses soft (gradient) similarity matching for experience comparison rather than hard binary matching. |
| `UseSimpleFlow` | `bool` | `false` | When `true`, uses a simplified extraction flow (no LLM reflection). Faster but less nuanced. |
| `EnableLlmRerank` | `bool` | `false` | When `true`, uses LLM to rerank retrieved experiences. Increases quality but adds latency. |
| `EnableLlmRewrite` | `bool` | `false` | When `true`, uses LLM to rewrite extracted memories for clarity. Increases quality but adds latency. |

---

## Llm — Language Model Backend

The `Llm` section configures which LLM provider is active and its connection details.

### Provider Selection

| Key | Type | Default | Description |
|---|---|---|---|
| `Provider` | `string` | `"ZAi"` | Active LLM provider: `"ZAi"`, `"Ollama"`, or `"LlamaCpp"`. Switching requires only a config change. |

### ZAi

| Key | Type | Default | Description |
|---|---|---|---|
| `ZAi:ApiKey` | `string` | `""` | API key. Overridden by `ZAI_API_KEY` env var if set. |
| `ZAi:Endpoint` | `string` | `"https://api.z.ai/api/paas/v4/"` | API endpoint URL. |
| `ZAi:ChatModel` | `string` | `"glm-5-turbo"` | Model used for chat completions. |
| `ZAi:LightModel` | `string?` | `null` | Optional lighter model for fast operations. Falls back to `ChatModel` if null. |

### Ollama

| Key | Type | Default | Description |
|---|---|---|---|
| `Ollama:ApiKey` | `string` | `"ollama"` | API key (any non-empty string works with local Ollama). |
| `Ollama:Endpoint` | `string` | `"http://localhost:11434/v1/"` | Ollama OpenAI-compatible endpoint. |
| `Ollama:ChatModel` | `string` | `"llama3.1"` | Model name as registered in Ollama. |
| `Ollama:LightModel` | `string?` | `null` | Optional lighter model. |

### LlamaCpp

| Key | Type | Default | Description |
|---|---|---|---|
| `LlamaCpp:ApiKey` | `string` | `"llamacpp"` | API key (server often ignores it). |
| `LlamaCpp:Endpoint` | `string` | `"http://localhost:8080/v1/"` | llama.cpp server OpenAI-compatible endpoint. |
| `LlamaCpp:ChatModel` | `string` | `"default"` | Model identifier. |
| `LlamaCpp:LightModel` | `string?` | `null` | Optional lighter model. |

### Per-Subsystem Overrides

| Key | Type | Default | Description |
|---|---|---|---|
| `EntityRefinement` | `object?` | `null` | Override LLM settings for entity/triple refinement. Null fields inherit from the active provider. |
| `Experiences` | `object?` | `null` | Override LLM settings for the experience pipeline. Null fields inherit from the active provider. |

Each override object supports: `Endpoint`, `Model`, `ApiKey`, `Enabled` (bool).

**Example — use a local model for experiences:**

```json
"Llm": {
  "Provider": "ZAi",
  "Experiences": {
    "Endpoint": "http://localhost:11434/v1/",
    "Model": "llama3.1",
    "Enabled": true
  }
}
```

---

## Halls — Room Classification Keywords

The `Halls` section maps room names to keyword lists used by `HallDetector` to auto-classify `AddMemory` content into rooms when no explicit `room` is provided.

Each key is a room name (kebab-case), and the value is an array of trigger keywords (lowercase). If content contains any keyword, it's classified into that room.

```json
"Halls": {
  "security": ["auth", "token", "jwt", "oauth"],
  "database": ["sql", "query", "schema", "migration", "duckdb"]
}
```

Add new rooms or keywords to improve auto-classification for your domain.

---

## Models — Embedding Configuration

The `Models` section configures the local embedding model used for semantic search.

| Key | Type | Default | Description |
|---|---|---|---|
| `EmbeddingModel:OnnxPath` | `string` | `"models/embeddinggemma/model_quantized.onnx"` | Path to the ONNX model file. |
| `EmbeddingModel:TokenizerPath` | `string` | `"models/embeddinggemma/tokenizer.model"` | Path to the SentencePiece tokenizer. |
| `EmbeddingModel:MaxSeqTokens` | `int` | `2048` | Maximum input tokens per embedding call. |
| `EmbeddingModel:EmbeddingDim` | `int` | `512` | Output embedding dimension after mean pooling. |
| `EmbeddingModel:ModelOutputDimension` | `int` | `768` | Raw model output dimension (before pooling/reduction). |

---

## Logging

Standard ASP.NET logging configuration. Key tip: set `"Wendmem"` to `"Debug"` for verbose operation logs.

---

## Environment Variable Overrides

Every setting can be overridden via environment variables using `__` (double underscore) as the hierarchy separator:

```powershell
# Palace settings
$env:Palace__DefaultWing = "personal"
$env:Palace__ForceDefaultWing = "true"
$env:Palace__AdmissionDuplicateThreshold = "0.95"

# LLM provider switch
$env:Llm__Provider = "Ollama"
$env:Llm__Ollama__Endpoint = "http://my-server:11434/v1/"

# Experience tuning
$env:Experiences__EnableLlmRerank = "true"
```

This is useful for running multiple wendmem instances with different configurations without modifying `appsettings.json`.
