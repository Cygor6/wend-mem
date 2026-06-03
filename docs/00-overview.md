# Wendmem — Overview

Wendmem is a local-first knowledge server that runs as an MCP (Model Context Protocol) server. AI agents connect to it via stdio to store, search, and synthesize knowledge across sessions.

## Data Model

```
Palace (singleton — your whole knowledge base)
├── Wing              — named namespace (e.g. "work", "personal", "project-x")
│   ├── Room          — auto-classified category (e.g. "code", "docs", "conversation")
│   │   ├── Drawer    — one chunk of source text (~800 chars) with embedding + metadata
│   │   └── Closet    — compressed summary of a drawer (A3K compression)
│   ├── Wiki Page     — LLM-authored synthesis with citations back to drawers
│   ├── KG Triple     — temporal fact (subject–predicate–object with valid_from/valid_to)
│   ├── Pending Update — queued suggestion that a wiki page may need revision due to new evidence
│   └── Activity Log   — append-only record of palace operations (mine, wiki_write, prune, distill, etc.)
```

### Key Concepts

| Concept | What it is |
|---------|-----------|
| **Drawer** | The atomic unit. One chunk of text with a vector embedding. Two types: `source` (mined from files) and `synthesis` (LLM-written summaries). |
| **Closet** | Compressed representation of a drawer content. Not directly searchable. |
| **Wiki Page** | Persistent synthesis document. Markdown body with `[[wikilinks]]` and citations to source drawers. Lint-checked for broken citations. |
| **KG Triple** | Temporal fact: `subject predicate object` with optional `valid_from`. Invalidatable when no longer true. |
| **Wing** | Namespace for isolation. Each project, person, or domain gets its own wing. |
| **Room** | Auto-classified category within a wing. Determined by file path (code, docs, conversation, etc.) |
| **Pending Update** | When new drawers overlap semantically with existing wiki pages, a pending update is queued. Agents review and decide whether to update the page. |
| **Activity Log** | Append-only log of all palace operations. Used for session context in WakeUp and for compounding-loop workflows. |
| **Wiki Lint** | 7-rule structured check over wiki pages: broken citations, orphan pages, stale pages, missing cross-links, gap candidates, pending updates, and contradiction candidates. |

### Drawer Identity

- Drawer ID = first 16 hex chars of SHA-256(content). Deterministic — identical content always produces the same ID.
- `AddDrawerAsync` uses `ON CONFLICT DO NOTHING` — duplicate content is silently skipped.
- `UpsertDrawerAsync` uses `ON CONFLICT DO UPDATE` — replaces content, used for synthesis drawers.

### Drawer Lifecycle

```
[New] → is_representative = TRUE → [Active — visible in search]
                                      │
                               Prune (GAC) → is_representative = FALSE → [Retired — invisible but recoverable]
```

Prune never hard-deletes. It sets `is_representative = FALSE`. All search queries filter `WHERE is_representative = TRUE`.

## Architecture

```
Files/Conversations ──→ FileMiner / ConversationMiner ──→ DrawerStorage (DuckDB)
                                          │                        │
                                          ├── PendingUpdateService ┘ (queues wiki page review)
                                          └── ActivityLog
                                                                    │
User Query ──────────→ PalaceSearcher ──→ WakeUp / Search ────────┘
                              │
                              ├── L0: Synthesis drawers (all, unlimited budget)
                              ├── L1: Recent source drawers (5 most recent)
                              ├── L2: Semantic search with MMR diversification
                              └── Pending Updates: top pages with queued evidence

MCP Client (Goose/Zed) ──→ DrawerTools / WikiTools / KGTools / WikiMaintenanceTools ──→ Services
                                      │
                                      ├── LintWiki (7-rule structured action list)
                                      ├── Distill (session-end filing decision)
                                      └── palace://schema resource (wing info + conventions)
```

## Tech Stack

- **Runtime**: .NET 10, AOT-compatible
- **Database**: DuckDB (embedded, zero-config)
- **Embeddings**: EmbeddingGemma-300M (768-dim native → 512-dim Matryoshka truncation)
- **Search**: BM25 (FTS with Porter stemmer) + cosine similarity + Reciprocal Rank Fusion
- **Protocol**: MCP over stdio

## Cluster Geometry

Wendmem v1.2 introduced geometry-aware consolidation (GAC). Every drawer gets cluster metadata:

| Column | Meaning |
|--------|---------|
| `cluster_id` | Which cluster this drawer belongs to |
| `cluster_d_bar` | Mean pairwise cosine distance within cluster (d̄_C) |
| `cluster_d_eff` | Effective dimensionality via participation ratio |
| `is_representative` | Whether this drawer appears in search results |

The governing inequality: ε_id ≥ 1 − c₁ · (θ' / d̄_C)^(d_eff/2)

Two regimes:
- **Tight** (d̄_C < θ'): Safe to consolidate. One medoid covers all cluster members.
- **Spread** (d̄_C ≥ θ'): Identity collapse guaranteed. Multiple representatives required.

Search results carry a regime tag (`Tight`, `Spread`, or `Unknown`) so agents can weight trust.

## Compounding Loop

Wendmem's wiki compounds over time without auto-rewriting. The loop works in three stages:

### Ingestion triggers
When `FileMiner` or `ConversationMiner` adds new drawers, it:
1. Computes pairwise cosine similarity between new drawer embeddings and existing wiki page embeddings.
2. Queues matches above 0.55 threshold as **pending updates** in `wiki_pending_updates`.
3. Logs the mining activity in `palace_activity`.

### Agent review
The agent reviews pending updates and lint findings, then decides whether to act:
- `ListPendingUpdates` — see which pages have new evidence.
- `LintWiki` — get a structured action list (7 rules, see below).
- `DismissPendingUpdate` — skip a pending update that isn't relevant.

### WikiLint rules

| Rule | Severity | What it detects |
|------|----------|----------------|
| BrokenCitation | error | Cited drawer doesn't exist or is retired |
| OrphanPage | warn | No inbound or outbound [[wikilinks]] |
| StalePage | warn | All cited drawers are retired |
| MissingCrossLink | info | Page mentions another page's title without [[wikilink]] |
| GapCandidate | info | KG entity with >= 5 triples but no wiki page |
| PendingUpdates | info | Page has >= 3 unresolved pending updates |
| ContradictionCandidate | warn | Pending drawer with semantic overlap and conflicting numeric KG triple |

### Session-end distillation
The `Distill` tool returns candidate pages and a draft scaffold — the agent then calls `WikiWrite` to persist. No LLM-authored auto-rewrites. The agent always decides.
