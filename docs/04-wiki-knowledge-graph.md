# Wiki and Knowledge Graph

Two persistent knowledge structures beyond raw drawers: wiki pages (synthesis documents) and KG triples (temporal facts).

## Wiki Pages

Wiki pages are LLM-authored synthesis documents with citations back to source drawers.

### WikiWrite - Create or Update

```
WikiWrite(
  path: "architecture/storage",
  wing: "work",
  title: "Storage architecture",
  content: "## Overview\n\nWendmem uses DuckDB for embedded storage...\n\nSee also [[architecture/search]].",
  citations: "a3f2b1c8d4e5f607,c8e4d2a1b91f0a7e",
  agent: "goose"
)
```

**Rules:**
- Path is kebab-case ASCII, `/` for hierarchy. Stable identity - same path overwrites.
- Citations are comma-separated drawer IDs (16 hex chars each). Must reference existing drawers.
- Content supports `[[wikilink]]` syntax - backlinks are computed during lint.
- Aim for 200-600 words. Split larger topics into sub-pages.
- Title in sentence case, not Title Case.

### WikiRead - Read a Page

```
WikiRead(path: "architecture/storage")
```

Returns the page content, citations list, backlinks, and metadata.

### WikiSearch - Search Pages

```
WikiSearch(query: "database schema", wing: "work", limit: 10)
```

Hybrid BM25 + semantic search over wiki pages. Ranks by relevance, not recency. Use when WakeUp's page index doesn't surface what you need.

### WikiLint — Structured Quality Check

```
LintWiki(wing: "work")
```

Returns a structured report with findings across 7 rules:

| Rule | Severity | What it detects |
|------|----------|-----------------|
| BrokenCitation | error | Cited drawer doesn't exist or is retired |
| OrphanPage | warn | No inbound or outbound [[wikilinks]] |
| StalePage | warn | All cited drawers are retired |
| MissingCrossLink | info | Page mentions another page's title without [[wikilink]] |
| GapCandidate | info | KG entity with >= 5 triples but no wiki page |
| PendingUpdates | info | Page has >= 3 unresolved pending updates |
| ContradictionCandidate | warn | Pending drawer with semantic overlap and conflicting numeric KG triple |

Returns: JSON with `wing`, `page_count`, `findings` array (rule, severity, page_path, message, details), and `counts_by_rule`.

The linter never auto-rewrites. It surfaces findings for agent or human review.

### When to Write Wiki Pages

| When | Why |
|------|-----|
| Agent synthesizes a non-trivial answer from multiple drawers | The synthesis deserves persistence |
| A topic recurs across sessions | Compile it once, cite it forever |
| User asks for documentation | Wiki pages ARE the documentation |
| Agent discovers a pattern worth recording | Patterns with citations are reusable |

### When NOT to Write Wiki Pages

- Raw facts - use `AddMemory` or `AddTriple` instead.
- Ephemeral session context - it won't be useful later.
- Single-drawer summaries - just cite the drawer directly.

## Pending Updates

When new drawers arrive (via mining or `AddMemory`), wendmem checks for semantic overlap with existing wiki pages. Matches above 0.55 cosine similarity are queued as pending updates.

### ListPendingUpdates

```
ListPendingUpdates(wing: "work", pagePath: "architecture/storage", limit: 50)
```

Returns pending update rows: page_path, drawer_id, similarity score, queue timestamp. Unresolved only.

### DismissPendingUpdate

```
DismissPendingUpdate(pagePath: "architecture/storage", drawerId: "a3f2b1c8d4e5f607")
```

Marks a pending update as dismissed. Use when the new evidence isn't relevant to the page.

### Auto-resolution

When `WikiWrite` is called with citations, all pending updates for the cited drawer IDs are automatically resolved with resolution `"cited"`. No manual dismissal needed for drawers that were incorporated into the page.

## Activity Log

The activity log records palace operations in an append-only table. Available via the CLI:

```
wendmem activity --wing work --limit 20
```

Returns recent entries with timestamp, wing, action, target, agent, and summary. Actions include: `mine`, `wiki_write`, `prune`, `distill`, `add_triple`, `invalidate_triple`.

## Distill — Session-End Filing

```
Distill(wing: "work", sessionSummary: "Refactored storage layer", pageHints: "architecture/storage")
```

Returns:
- `candidate_pages`: existing pages that match the session summary, with pending drawer IDs
- `new_page_scaffold`: suggested_path (kebab-case), suggested_title (sentence case), draft_outline (markdown with `<TODO>` placeholders)
- `next_action`: instruction to call `WikiWrite`

The agent decides: update an existing page, create a new one, or do nothing. Distill never writes automatically.

## Palace Schema Resource

The MCP resource `palace://schema` is auto-generated from live data. It provides:
- Wing names and room counts
- Routing keywords (hall detector mappings)
- Wiki conventions (citation rules, naming standards)
- Workflow instructions for agents (WakeUp → work → Distill → WikiWrite)

Agents should read this at session start via `ReadResource("palace://schema")`.

## Knowledge Graph

The KG stores temporal facts as triples: `subject - predicate - object`.

### AddTriple - Record a Fact

```
AddTriple(
  subject: "user",
  predicate: "works_at",
  obj: "acme",
  validFrom: "2024-01-15"    // optional, defaults to today
)
```

**Creates entities automatically.** If `user` or `acme` don't exist, they're created.

**Common predicates:** `works_at`, `uses`, `depends_on`, `lives_in`, `child_of`, `owns`, `prefers`, `member_of`.

**Auto-extracted predicates** (via `NumericFactExtractor` during mining):

| Predicate | Pattern | Example |
|-----------|---------|---------|
| `has_param_count` | `func() takes N` | `Parse() takes 3` |
| `has_version` | `name v1.2.3` | `DuckDB v1.5.0` |
| `returns_type` | `returns Type` | `returns Task<string>` |
| `has_timeout` | `timeout: N unit` | `timeout: 30s` |
| `uses_port` | `port: N` | `port: 5432` |
| `has_value` | `chunk_size: N` | `chunk_size: 800` |

### InvalidateTriple - Retire a Fact

```
InvalidateTriple(
  subject: "user",
  predicate: "works_at",
  obj: "acme"
)
```

Sets `valid_to = today`. The triple stays in history (it WAS true) but no longer appears in WakeUp's active facts.

**Always pair with a new AddTriple** if you're recording what's now true:

```
InvalidateTriple(subject: "user", predicate: "works_at", obj: "acme")
AddTriple(subject: "user", predicate: "works_at", obj: "globex")
```

### KG as Active Search Channel

The KG is not write-only — it actively participates in search retrieval. During `HybridSearchAsync`:

1. Query tokens are extracted and matched against KG entity names (`MatchEntitiesInTextAsync`).
2. Matching entity triples yield drawer IDs via `LookupEntitiesForQueryAsync`.
3. KG drawer IDs are merged into RRF scoring alongside BM25 and cosine results.
4. This preserves predicate directionality — "A calls B" is distinct from "B calls A".

### NumericFactExtractor

During file and conversation mining, `NumericFactExtractor` automatically extracts structured numeric facts from drawer content and stores them as KG triples. This populates the KG with machine-readable facts that enable precise matching:

- **Arity facts**: function signatures → `has_param_count` triples
- **Version facts**: library versions → `has_version` triples
- **Return types**: method return types → `returns_type` triples
- **Timeouts**: timeout values → `has_timeout` triples
- **Ports**: port numbers → `uses_port` triples
- **Config values**: chunk sizes, limits → `has_value` triples

The extractor runs fire-and-forget after each drawer insertion — it never blocks or fails the mining pipeline.

### When to Use KG Triples

| When | Example |
|------|---------|
| User states a relationship | "I work at Acme" → `user works_at acme` |
| Project dependencies | "wendmem depends on duckdb-net" |
| Preferences | "user prefers dark theme" |
| People relationships | "alice child_of bob" |
| Tool/service usage | "project-x uses postgres" |

### When NOT to Use KG Triples

- Long-form content - use `AddMemory` or `WikiWrite`.
- Things that are already in files - just mine the files.
- Opinions or uncertain information - the KG records facts.

## How WakeUp Uses These

WakeUp loads KG facts and wiki page paths alongside drawers:

```json
{
  "pages": [
    {"path": "architecture/storage", "title": "Storage architecture"},
    {"path": "auth-flow", "title": "Authentication flow"}
  ],
  "facts": [
    {"subject": "user", "predicate": "works_at", "object": "acme"},
    {"subject": "wendmem", "predicate": "uses", "object": "duckdb"}
  ],
  "activity": ["last 5 actions..."],
  "pending_updates": [
    {"page_path": "architecture/storage", "candidate_count": 4},
    {"page_path": "auth-flow", "candidate_count": 2}
  ]
}
```

The agent sees active facts and available wiki pages at session start, then uses `WikiRead` or `SearchMemories` to drill deeper as needed. The `pending_updates` field shows pages with the most queued new evidence. Agents should consider addressing these during the session.
