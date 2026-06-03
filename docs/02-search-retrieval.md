# Search and Retrieval

Wendmem has three retrieval modes: WakeUp (session start), SearchMemories (targeted query), and Grep (contextual window).

## WakeUp - Session Start Context

```
WakeUp(wing: "work", seedQuery: "database schema")
```

The primary retrieval mechanism. Called at the start of every session to prime the agent's context.

### Three-Layer Retrieval

| Layer | Source | What you get | Budget |
|-------|--------|-------------|--------|
| L0 | Synthesis drawers | All synthesis pages for the wing | Unlimited |
| L1 | Recent source | 5 most recent source drawers | 3200 chars shared with L2 |
| L2 | Semantic MMR | Up to 10 diverse, relevant drawers | 3200 chars shared with L1 |

### How L2 Works (MMR Reranking)

1. Embed the seed query.
2. Over-fetch: request `k × 3` results via `HybridSearchAsync` (BM25 + cosine + KG channel).
3. Exclude any IDs already in L0 or L1.
4. Apply side-index boost (F2): Jaccard overlap between query tokens and drawer structured tokens.
5. Apply Maximal Marginal Relevance via `DrawerStorage.MmrRerank`:
   - For each candidate: `score = lambda * c.Score - (1 - lambda) * redundancy`
   - `c.Score` = pre-computed score (from RRF + side-index + KG boosts)
   - `redundancy` = max cosine similarity to any already-selected result
   - `lambda = 0.5` (configurable via `PalaceConfig.MmrLambda`)
   - Null embeddings treated as similarity 0

### Output Format

```
[work/architecture] (synthesis)
This project uses DuckDB for embedded storage...

[work/docs]                           ← L1 (recent)
Added wiki page caching layer...

[work/code]                           ← L2 (semantic)
The DrawerStorage class handles all persistence...

---
{"pages": [...], "facts": [...], "activity": [...]}
```

## SearchMemories - Targeted Search

```
SearchMemories(query: "DuckDB parameter syntax", wing: "work", room: "code", k: 10)
```

Hybrid search combining BM25 text search, cosine similarity, and KG entity matching via Reciprocal Rank Fusion.

### How it works

1. Three parallel searches, each returning up to 200 results:
   - BM25 on drawer FTS text
   - BM25 on closet FTS text (compressed summaries)
   - Cosine similarity on embeddings
2. Extract query tokens (split on whitespace/punctuation, lowercase, discard <3 chars and stop words).
3. KG channel: query tokens matched against entity names in the knowledge graph. Matching triples yield drawer IDs.
4. RRF fuse the three core channels: `score[id] += 1.0 / (60 + rank + 1)` from each list.
5. Merge KG drawer IDs into candidate set:
   - Existing candidates: `score += 1.0 / (60 + kg_rank + 1)`
   - KG-only drawers: fetched and inserted with that as starting score
6. Re-sort by RRF score.
7. Apply MMR reranking via `DrawerStorage.MmrRerank` (lambda from `PalaceConfig.MmrLambda`).
8. Return top `k` results.

### When to use

- Agent needs specific information on a topic.
- WakeUp didn't surface what you need.
- Searching across all drawer types (source + synthesis).

### Parameters

| Param | Required | Default | Description |
|-------|----------|---------|-------------|
| `query` | Yes | - | Natural language search query |
| `wing` | No | All wings | Scope to one wing |
| `room` | No | All rooms | Scope to one room |
| `k` | No | 10 | Number of results |

## Grep - Contextual Window (CLI)

```bash
wendmem grep "error handling" --wing work --context 5
```

Returns a temporal window of drawers around the most relevant anchor.

### How it works

1. Find the single best anchor drawer via hybrid search.
2. Return N drawers before and N after the anchor (ordered by `mined_at`).
3. Total: up to `2 × contextWindow + 1` drawers in chronological order.

### When to use

- Understanding the context around a specific piece of code or text.
- Reading a conversation thread in order.
- Reconstructing the narrative around a topic.

### Parameters

| Param | Required | Default | Description |
|-------|----------|---------|-------------|
| `query` | Yes | - | Query to find the anchor |
| `wing` | No | All | Scope |
| `room` | No | All | Scope |
| `contextWindow` | No | 3 | Drawers before and after anchor |

## GetDrawer - Read by ID

```
GetDrawer(id: "a3f2b1c8d4e5f607")
```

Returns a single drawer by its 16-hex-char ID. Use when you have a citation reference from a wiki page or search result.

## Search Pipeline

The retrieval pipeline addresses three failure modes that pure cosine similarity cannot handle:

### F2 — Structured Side-Index (Numeric/Entity Confusion)

Pure cosine cannot distinguish "takes 3 args" from "takes 5 args". The `EntityIndexService` extracts typed tokens (numbers, qualified names, hex literals, arities, function calls) from drawer content at insert time. At query time, Jaccard overlap between query tokens and candidate drawer tokens adds a 0.15 score boost, breaking ties between numerically similar but semantically different content.

### F3 — KG as Active Search Channel (Role-Swap Confusion)

"A calls B" and "B calls A" are indistinguishable under cosine. The knowledge graph now participates as an active search channel in `HybridSearchAsync`. Query tokens are matched against KG entity names; matching triples yield drawer IDs merged into RRF scoring. This preserves predicate directionality — subject→predicate→object — preventing role-swap confusion.

### F4 — MMR Post-Processing (Hubness)

Boilerplate drawers (error messages, logging templates) dominate results at up to 4.1× expected frequency. `DrawerStorage.MmrRerank` applies Maximal Marginal Relevance as the final pipeline stage, using `lambda * relevance - (1 - lambda) * redundancy` to diversify results while maintaining relevance. Configurable via `PalaceConfig.MmrLambda` (default 0.5).

### Pipeline Order

```
WakeUp L2:    HybridSearchAsync → exclude L0/L1 → F2 side-index boost → F4 MMR
SearchMem:    HybridSearchAsync (BM25+cosine+KG, MMR built-in) → F2 boost → F3 KG boost → F4 MMR
```

## Search Comparison

| Method | Best for | Returns | Ordered by |
|--------|----------|---------|-----------|
| `WakeUp` | Session start | L0+L1+L2 synthesis+source | Layer priority |
| `SearchMemories` | Targeted query | Source + synthesis | RRF score + MMR |
| `Grep` (CLI) | Context/narrative | Chronological window | `mined_at` |
| `GetDrawer` | Known ID | Single drawer | - |

## Performance Characteristics

| Operation | Latency | Notes |
|-----------|---------|-------|
| WakeUp | ~200ms | 3 parallel queries + KG channel + side-index + MMR |
| SearchMemories | ~150ms | 3 parallel queries + RRF + KG channel + side-index + MMR |
| Grep | ~150ms | Hybrid search + temporal window |
| All searches | - | Filter: `is_representative = TRUE AND valid_to IS NULL` |
