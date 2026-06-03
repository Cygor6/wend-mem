# MCP Tool Reference

17 tools, grouped by subsystem. All tools are available to MCP clients (Goose, Zed, Claude Desktop).

## Drawer Tools (5)

### WakeUp

Get the palace map — synthesis pages, recent drawers, and semantic search results for session context.

```
WakeUp(wing: "work", seedQuery: "database schema")
```

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| wing | string | No | Scope to one wing. Recommended. |
| seedQuery | string | No | Query for semantic layer. If omitted, only L0+L1 are returned. |

Returns: formatted text with L0 synthesis, L1 recent, L2 semantic results with regime tags, plus JSON with wiki pages, KG facts, and activity log.

### SearchMemories

Hybrid BM25 + cosine search over raw drawer content.

```
SearchMemories(query: "DuckDB parameter syntax", wing: "work", room: "code", k: 10)
```

| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| query | string | Yes | — | Natural language query |
| wing | string | No | All | Scope to wing |
| room | string | No | All | Scope to room |
| k | int | No | 10 | Max results |

Returns: JSON array of `{id, wing, room, content, source, regime}`.

### GrepExact

Exact string or regex search over raw drawer content.

```
GrepExact(pattern: "(?i)DrawerStorage", wing: "work", room: "code", k: 20)
```

| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| pattern | string | Yes | — | Exact string or regex (DuckDB RE2) |
| wing | string | No | All | Scope to wing |
| room | string | No | All | Scope to room |
| k | int | No | 20 | Max results |

Returns: JSON array of matching drawers.

### GetDrawer

Read a single drawer by ID.

```
GetDrawer(id: "a3f2b1c8d4e5f607")
```

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| id | string | Yes | 16-hex-char drawer ID |

Returns: JSON with drawer content, metadata, and source.

### AddMemory

Store a new memory drawer.

```
AddMemory(text: "User prefers dark theme", wing: "personal", room: "preferences")
```

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| text | string | Yes | Text to store |
| wing | string | Yes | Wing namespace |
| room | string | No | Room category |

Returns: JSON with the new drawer's ID.

## Knowledge Graph Tools (2)

### AddTriple

Record a temporal fact.

```
AddTriple(subject: "user", predicate: "works_at", obj: "acme", validFrom: "2024-01-15")
```

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| subject | string | Yes | Subject entity (lowercase, auto-created) |
| predicate | string | Yes | Relation in snake_case |
| obj | string | Yes | Object entity or literal |
| validFrom | string | No | ISO date (YYYY-MM-DD), defaults to today |

Returns: JSON with triple ID and contents.

### InvalidateTriple

Mark a fact as no longer true.

```
InvalidateTriple(subject: "user", predicate: "works_at", obj: "acme")
```

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| subject | string | Yes | Must match existing triple |
| predicate | string | Yes | Must match existing triple |
| obj | string | Yes | Must match existing triple |

Returns: JSON confirmation.

## Wiki Tools (3)

### WikiRead

Read a wiki synthesis page.

```
WikiRead(path: "architecture/storage")
```

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| path | string | Yes | Page path (kebab-case, `/` for hierarchy) |

Returns: JSON with content, citations, backlinks, metadata.

### WikiWrite

Create or update a wiki synthesis page.

```
WikiWrite(
  path: "architecture/storage",
  wing: "work",
  title: "Storage architecture",
  content: "## Overview\n\nUses DuckDB for...\n\nSee [[architecture/search]].",
  citations: "a3f2b1c8d4e5f607,c8e4d2a1b91f0a7e",
  agent: "goose"
)
```

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| path | string | Yes | Page path. Stable identity. |
| wing | string | Yes | Wing namespace |
| title | string | Yes | Human-readable title (sentence case) |
| content | string | Yes | Markdown body. Supports `[[wikilinks]]`. |
| citations | string | Yes | Comma-separated drawer IDs |
| agent | string | No | Agent identifier for audit log |

Returns: JSON with path and "created"/"updated" status.

### WikiSearch

Search wiki pages by content.

```
WikiSearch(query: "database schema", wing: "work", limit: 10)
```

| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| query | string | Yes | — | Search query |
| wing | string | No | All | Scope to wing |
| limit | int | No | 10 | Max results |

Returns: JSON array of matching pages with path, title, wing.

## Wiki Maintenance Tools (4)

### ListPendingUpdates

List wiki pages with new drawer evidence queued for review.

```
ListPendingUpdates(wing: "work", pagePath: "architecture/storage", limit: 50)
```

| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| wing | string | Yes | — | Wing to scope to |
| pagePath | string | No | All pages | Filter to a specific page |
| limit | int | No | 50 | Max results |

Returns: JSON with `pending_updates` array of `{page_path, drawer_id, similarity, queued_at}`.

### DismissPendingUpdate

Dismiss a pending update without applying it.

```
DismissPendingUpdate(pagePath: "architecture/storage", drawerId: "a3f2b1c8d4e5f607")
```

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| pagePath | string | Yes | Page path of the pending update |
| drawerId | string | Yes | Drawer ID of the pending update |

Returns: JSON with `{dismissed: true, page_path, drawer_id}`.

### LintWiki

Lint the wiki and return a structured action list with findings.

```
LintWiki(wing: "work")
```

| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| wing | string | No | All wings | Scope to wing |

Returns: JSON with `{wing, page_count, findings: [{rule, severity, page_path, message, details}], counts_by_rule}`.

Findings span 7 rules:

| Rule | Severity | What it detects |
|------|----------|----------------|
| BrokenCitation | error | Cited drawer doesn't exist or is retired |
| OrphanPage | warn | No inbound or outbound [[wikilinks]] |
| StalePage | warn | All cited drawers are retired |
| MissingCrossLink | info | Page mentions another page's title without [[wikilink]] |
| GapCandidate | info | KG entity with >= 5 triples but no wiki page |
| PendingUpdates | info | Page has >= 3 unresolved pending updates |
| ContradictionCandidate | warn | Pending drawer with semantic overlap and conflicting numeric KG triple |

### Distill

Decide whether to file a session's insights into the wiki, and prepare to do so. Call BEFORE declaring a non-trivial task complete.

```
Distill(wing: "work", sessionSummary: "Refactored storage layer to use connection pooling", pageHints: "architecture/storage")
```

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| wing | string | Yes | Wing |
| sessionSummary | string | Yes | One-paragraph summary of what was learned/decided |
| pageHints | string | No | Comma-separated page paths to consider |

Returns: JSON with `{wing, candidate_pages: [{path, pending_drawer_ids} or {drawer_id, score}], new_page_scaffold: {suggested_path, suggested_title, draft_outline}, next_action}`.

The scaffold contains `<TODO>` placeholders — the agent fills these in before calling `WikiWrite`. No auto-rewrites.

## Episode Tools (2)

### RecordEpisode

Record a task episode at session end: goal, outcome, and what to do differently next time. Call BEFORE `Distill` when a non-trivial task completes (success or failure). Skip for trivial Q&A or single-tool lookups.

```
RecordEpisode(
  wing: "work",
  goal: "Migrate storage layer to DuckDB",
  plan: "Replaced SQLite calls one service at a time, tested after each step.",
  outcome: "success",
  whatWorked: "Incremental migration with per-step tests; GrepExact to find all call sites",
  whatFailed: "Initial attempt to do it all at once caused cascading failures",
  nextTime: "Always migrate one service at a time and test before moving on",
  drawerRefs: "a3f2b1c8d4e5f607,c8e4d2a1b91f0a7e"
)
```

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| wing | string | Yes | Wing namespace |
| goal | string | Yes | What the agent was trying to accomplish (1-2 sentences) |
| plan | string | Yes | Approach taken (1 paragraph) |
| outcome | string | Yes | `"success"` \| `"partial"` \| `"failure"` |
| whatWorked | string | Yes | Specific tools, patterns, or sources that helped |
| whatFailed | string | Yes | What did not work — be specific |
| nextTime | string | Yes | Concrete guidance for a future agent facing a similar task |
| drawerRefs | string | No | Comma-separated drawer IDs touched |
| skillRefs | string | No | Comma-separated skill IDs used |

Returns: JSON with episode `id`, `wing`, `outcome`, and `nextTime`.

### FindEpisodes

Find past episodes relevant to the current goal. `WakeUp` already returns the top 3 matching episodes — use this only for narrower lookups or outcome filtering.

```
FindEpisodes(query: "migrate storage layer", wing: "work", outcome: "failure", k: 5)
```

| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| query | string | Yes | — | Current goal or task description |
| wing | string | No | Default | Wing to search |
| outcome | string | No | `"any"` | Filter: `"success"` \| `"failure"` \| `"any"` |
| k | int | No | 3 | Max results |

Returns: JSON array of `{id, goal, outcome, nextTime, whatWorked, whatFailed, score}`.

## Skill Tools (1)

### FindSkills

Find skills (procedural SKILL.md guides) relevant to a goal. After this returns, read the SKILL.md file at the returned `path` using your file-reading tools — do not ask wendmem for its content. `WakeUp` already surfaces the top 3 skills for the seedQuery; use this for narrower lookups only.

```
FindSkills(query: "optimize DuckDB queries", wing: "work", k: 3)
```

| Param | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| query | string | Yes | — | Goal or task description |
| wing | string | No | Default | Wing to search |
| k | int | No | 3 | Max results |

Returns: JSON array of `{id, name, path, description, successCount, failureCount, successRate}`.

## Resources (1)

### palace://schema

Auto-generated schema document for this palace. Includes wing names, room counts, routing keywords, wiki conventions, and workflow instructions.

Agents read this via `ReadResource("palace://schema")` at session start to understand the palace structure and conventions.
