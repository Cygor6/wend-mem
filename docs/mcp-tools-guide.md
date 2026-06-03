# Wendmem MCP Tools — Complete Reference & Usage Guide

## Overview

Wendmem exposes **17 MCP tools** organized into 6 groups. Every tool returns a structured JSON envelope. The agent should always check `success`, then `decision_support.can_proceed` and `decision_support.suggested_action` before deciding what to do next.

> This document is the internal reference for wendmem maintainers. The agent-facing contract lives in `SKILL.md` and `references/tools.md`. Keep all three consistent when the API changes.

---

## Response Envelope (all tools)

Every tool returns:

```json
{
  "success": true,
  "result": { ... },
  "confidence": {
    "level": "high" | "medium" | "low",
    "score": 0.87,
    "reason": "exact_match" | "semantic_match" | "poor_match",
    "signals": {
      "bm25": true,
      "semantic": 0.87,
      "kg_entity": true
    },
    "agreement": "full" | "partial" | "single" | "not_applicable"
  },
  "decision_support": {
    "can_proceed": true,
    "suggested_action": "proceed" | "verify" | "retry" | "ask_user",
    "summary": "Human-readable explanation"
  },
  "error": null,
  "meta": {
    "tool": "ToolName",
    "duration_ms": 42
  }
}
```

**Field semantics:**

| Field | Type | Meaning |
|---|---|---|
| `success` | boolean | `true` on success; `false` on any error. Read `error` instead of `result` when false. |
| `result` | object \| null | Tool-specific payload. Null on error. |
| `confidence` | object \| null | **Only populated by `SearchMemories`.** Null for every other tool. |
| `decision_support` | object | Always present. The agent's primary decision input on non-SearchMemories tools. |
| `error` | object \| null | `{code, message}` when `success: false`. Null otherwise. |
| `meta.tool` | string | The tool name. |
| `meta.duration_ms` | number | End-to-end latency including DB and embedding work. |

**Confidence signals:**

| Signal | Type | Meaning |
|---|---|---|
| `bm25` | boolean | BM25 keyword channel contributed to the score |
| `semantic` | number | Cosine similarity from the vector channel (0.0–1.0) |
| `kg_entity` | boolean | Knowledge graph entity channel matched query tokens |

**Agreement levels:**

| agreement | Channels confirming | Action |
|---|---|---|
| `full` | All three (bm25 + semantic + kg_entity) | Trust level, follow `suggested_action` |
| `partial` | Two of three | Follow `suggested_action` |
| `single` | One only | **Always verify**, regardless of score |
| `not_applicable` | Channel signals don't apply | Ignore |

**Error codes:**

| error.code | Meaning | Recovery |
|---|---|---|
| `not_found` | ID or path doesn't exist | Broaden query or try alternate tool |
| `conflict` | Write conflicts with existing state | Read `result`, ask user |
| `invalid_input` | Malformed argument | Fix the call — never retry unchanged |
| `internal` | Server-side failure | Retry once, then surface to user |

---

## Session Lifecycle — The Correct Order

```
┌─────────────────────────────────────────────────────────┐
│                    SESSION START                         │
│                                                         │
│  1. WakeUp(wing, seedQuery)   ← ALWAYS FIRST            │
│     ↓ returns episodes + skills in JSON tail            │
│                                                         │
│  2. Read SKILL.md at each skill path                    │
│     Read next_time on each surfaced episode             │
│     ↓                                                   │
│                                                         │
│  3. <do work using retrieval + storage tools below>     │
│     ↓                                                   │
│                                                         │
│  4. AddMemory       ← user states a fact                │
│     AddTriple       ← named relationship confirmed      │
│     ↓                                                   │
│                                                         │
│  5. RecordEpisode   ← BEFORE Distill (non-trivial only) │
│     ↓                                                   │
│  6. Distill         ← BEFORE WikiWrite                  │
│     ↓                                                   │
│  7. WikiWrite       ← persist synthesis with citations  │
│                                                         │
│                   SESSION END                           │
└─────────────────────────────────────────────────────────┘
```

---

## Group 1: Retrieval (DrawerTools) — 6 tools

### 1. WakeUp — *Always call first*

**When:** Start of every session. No exceptions.

**What it returns** (text + JSON tail after `---` separator):

**Text section:**
- L0 synthesis drawers (pre-computed synthesis content)
- L1 recent source drawers (5 most recently accessed)
- L2 semantic source drawers (MMR-diversified, only if `seedQuery` provided)
- `## Pending Reviews` (wiki pages with new evidence)

**JSON tail:**

| Key | Content |
|---|---|
| `pages` | Wiki page index — `[{path, title}]` |
| `facts` | Active KG triples — `[{subject, predicate, object}]` |
| `episodes` | Top 3 past episodes matching `seedQuery` — `[{id, goal, outcome, next_time}]` |
| `skills` | Top 3 registered skills matching `seedQuery` — `[{id, name, path, description, success_rate}]` |
| `reflection_drafts` | Pending synthesis drafts — `[{id, suggested_path, suggested_title, question}]` |
| `activity` | Recent palace operations |

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `wing` | recommended | Project/namespace to scope to (e.g. `wendmem`, `myproject`) |
| `seed_query` | recommended | User's current question or task — activates L2 semantic layer + episodes + skills |

**Key behavior:**
- `wing` normalizes automatically (uppercase → lowercase, underscores → hyphens)
- If no `seed_query`, L2 is skipped and `episodes`/`skills` are empty
- `skills[].path` is a **folder on disk**. The agent reads `SKILL.md` at that path using its own file tools — wendmem does not return skill content
- Returns `confidence: null` — use `decision_support.summary`

---

### 2. SearchMemories — *Find raw source evidence*

**When:** You need exact phrasings, code snippets, error messages, or source-level evidence.

**What it does:** Hybrid BM25 + cosine semantic + KG entity lookup (RRF-fused), with side-index boost, salience boost, and MMR diversification.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `query` | ✅ | Natural language. Distinctive nouns beat abstract descriptions. |
| `wing` | recommended | Narrows corpus, improves relevance significantly |
| `room` | optional | Further scope below wing |
| `k` | optional | Max results (default 10, raise to 25 for broad surveys) |

**Key behavior:**
- Returns drawer IDs (16 lowercase hex chars), content, source file, wing/room, cluster regime
- Multi-signal agreement: bm25 + semantic + kg_entity → `full`/`partial`/`single`
- **Only tool that populates `confidence`**
- Confidence thresholds (corpus-specific, set in `palace-config.json`):

  | score | level | reason |
  |---|---|---|
  | > 0.80 | `high` | `exact_match` |
  | > 0.60 | `medium` | `semantic_match` |
  | > 0.40 | `low` | `semantic_match` |
  | ≤ 0.40 | `low` | `poor_match` |

  `can_proceed` is `true` when score > 0.40 AND results exist.

- If `MinRetrievalScore` configured and all scores below threshold → `success: false` with `error.code = "insufficient_evidence"`

**Agent should:** Use results as evidence. Pass drawer IDs as citations to WikiWrite.

---

### 3. GrepExact — *Exact string or regex search*

**When:** You know the precise term — a symbol name, method, error message, hex ID, SQL fragment, config key.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `pattern` | ✅ | RE2 regex. Prefix `(?i)` for case-insensitive. |
| `wing` | optional | Scope to wing |
| `room` | optional | Scope to room. Valid: `security`, `config`, `database`, `api`, `ui`, `testing`, `docs`, `devops`, `general`. Any other value returns zero silently. |
| `k` | optional | Max results (default 20) |

**Key behavior:**
- Faster and more precise than SearchMemories for symbol lookups (BM25 stems identifiers)
- Returns matching drawers with source file paths and context snippets
- Returns `confidence: null` — `can_proceed: true` when matches exist
- RE2 restrictions: no backreferences (`\1`), no lookaheads (`(?=...)`) — these pass .NET validation but fail silently at the DuckDB layer
- Invalid regex returns `error.code = "invalid_input"` immediately

**Agent should:** Use when you need to find an exact string, not a semantic concept.

---

### 4. GetDrawer — *Read one drawer by ID*

**When:** A search snippet isn't enough and you need the full verbatim content.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `id` | ✅ | Exactly 16 lowercase hex chars (from WakeUp/SearchMemories/citations) |

**Key behavior:**
- Read-only — drawers are immutable by design
- Returns `error.code = "not_found"` if ID doesn't exist
- Returns `confidence: null` — `can_proceed: true` when found

---

### 5. WikiRead — *Read a synthesis page*

**When:** You see a page path in WakeUp's `pages` index or a `[[wikilink]]` in another page.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `path` | ✅ | Kebab-case ASCII, slashes for hierarchy |

**Key behavior:**
- Returns full markdown content, citations (drawer IDs), backlinks
- Returns `confidence: null`

---

### 6. WikiSearch — *Search synthesis pages*

**When:** WakeUp's recency-ordered index doesn't surface what you need.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `query` | ✅ | Natural language query |
| `wing` | optional | Scope to wing |
| `limit` | optional | Max results (default 10) |

**Key behavior:**
- Ranks by relevance (not recency like WakeUp's index)
- Searches page content and titles
- Returns `confidence: null`

---

## Group 2: Storage — 4 tools

### 7. AddMemory — *Store verbatim text*

**When:** User shares context worth preserving exactly: a decision, error message, code snippet, key fact.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `text` | ✅ | Verbatim content — do NOT paraphrase or summarize |
| `wing` | ✅ | Project/namespace |
| `room` | optional | Subdivision (auto-detected via HallDetector if omitted) |
| `source` | optional | Origin file path, URL, or conversation URI |

**Key behavior:**
- Content-hash deduplication (identical content → same ID, no duplicate)
- Admission control: near-duplicate content (cosine > 0.97) rejected with `near_duplicate` reason
- Importance score computed automatically via heuristic salience scorer
- `can_proceed: false` means content was rejected — check `summary` for the matched drawer ID
- Returns `confidence: null`

**Agent should:** Store decisions, key facts, error messages verbatim. For your own synthesis, use WikiWrite.

---

### 8. AddTriple — *Record a persistent fact*

**When:** User states a relationship that should persist across sessions.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `subject` | ✅ | Entity name, lowercase (auto-created if new) |
| `predicate` | ✅ | Relation in `snake_case` (e.g. `depends_on`, `uses`, `works_at`) |
| `obj` | ✅ | Object entity or literal value (auto-created) |
| `valid_from` | optional | ISO date YYYY-MM-DD (defaults to today) |

**Key behavior:**
- Entities created automatically if they don't exist
- Conflict detection returns `suggested_action = "verify"` with conflict described in `summary` — triple still recorded
- Triples surface in WakeUp's `facts` field
- Returns `confidence: null`

---

### 9. InvalidateTriple — *Retire a fact*

**When:** A previously-true relationship has changed.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `subject` | ✅ | Must match existing triple |
| `predicate` | ✅ | Must match existing triple |
| `obj` | ✅ | Must match existing triple |

**Key behavior:**
- Sets `valid_to` to today — triple stays in history but stops appearing in WakeUp
- Returns `confidence: null`, `can_proceed: true` on success
- **Always pair with AddTriple to record what is NOW true**

---

### 10. WikiWrite — *Create or update a synthesis page*

**When:** You've produced a non-trivial synthesis from multiple drawers, OR Distill returned a scaffold.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `path` | ✅ | Kebab-case ASCII path (stable identity — same path overwrites) |
| `wing` | ✅ | Project/namespace |
| `title` | ✅ | Sentence case (e.g. `Wakeup design`, NOT `Wakeup Design`) |
| `content` | ✅ | Markdown body. Use `[[other-path]]` for wiki links. 200-600 words. |
| `citations` | ✅ | Comma-separated drawer IDs (16 hex chars each, from SearchMemories/AddMemory) |
| `agent` | optional | Agent identifier for audit log |

**Key behavior:**
- Citations are validated — broken IDs are rejected
- `citations=""` silently succeeds — never omit real drawer IDs
- Same path = update (not error)
- Backlinks computed automatically during lint
- Returns `confidence: null`

---

## Group 3: Episodes (EpisodeTools) — 2 tools

### 11. RecordEpisode — *Record session outcome — call BEFORE Distill*

**When:** End of a non-trivial session where something was attempted (success or failure). Call BEFORE Distill.

**What it does:** Records goal, plan, outcome, what worked, what failed, and concrete guidance for future agents. WakeUp surfaces past episodes matching the seedQuery.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `wing` | ✅ | Project/namespace |
| `goal` | ✅ | What the agent was trying to accomplish (1-2 sentences) |
| `plan` | ✅ | Approach taken (1 paragraph) |
| `outcome` | ✅ | One of: `success`, `partial`, `failure` |
| `what_worked` | ✅ | Be specific — name tools, patterns, sources |
| `what_failed` | ✅ | Be specific — what did NOT work |
| `next_time` | ✅ | Concrete guidance to a future agent facing a similar task |
| `drawer_refs` | optional | Comma-separated drawer IDs touched this session |
| `skill_refs` | optional | Comma-separated skill IDs used |

**Key behavior:**
- Generates embedding from `goal + next_time` for future semantic retrieval
- Updates skill `success_count`/`failure_count` if `skill_refs` provided
- `failure` episodes get a +0.05 retrieval boost (failures are especially valuable)
- WakeUp surfaces top 3 episodes matching `seedQuery`
- Returns `confidence: null`

**Do NOT call for:** trivial Q&A, single-tool lookups, sessions without a clear goal.

**Agent should:** Be honest about failures — `what_failed` + `next_time` are the most valuable fields.

---

### 12. FindEpisodes — *Search past episodes*

**When:** WakeUp already returns top 3 episodes — use this only for narrower scope or outcome filtering.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `query` | ✅ | Current goal or task description |
| `wing` | optional | Scope to wing |
| `outcome` | optional | Filter: `success`, `failure`, `any` (default `any`) |
| `k` | optional | Max results (default 3) |

**Key behavior:**
- Cosine match against episode embeddings, threshold 0.55
- Returns `confidence: null`

---

## Group 4: Skills (SkillTools) — 1 tool

### 13. FindSkills — *Find relevant skills*

**When:** Facing a procedural task. WakeUp already surfaces top 3 skills — use this for narrower lookups only.

**What it does:** Returns skill metadata including a folder path. **The agent must READ the `SKILL.md` file at the returned path** using its file-reading tools — wendmem does not return skill content via MCP.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `query` | ✅ | Goal or task description |
| `wing` | optional | Scope to wing |
| `k` | optional | Max results (default 3) |

**Key behavior:**
- Returns: `[{id, name, path, description, success_rate, success_count, failure_count}]`
- Results boosted by `success_rate = success_count / max(1, success_count + failure_count)` — well-tested skills rank higher
- Cosine match against `(name + ': ' + description)` embeddings, threshold 0.50
- Returns `confidence: null`

> Skills are registered, validated, updated, and removed via the wendmem CLI (`wendmem skills add | list | show | update | remove | reindex | validate | new`) — not via MCP tools. The agent only reads.

---

## Group 5: Wiki Maintenance — 4 tools

### 14. Distill — *Session boundary — MANDATORY before session end*

**When:** End of every non-trivial session. No exceptions. **Call AFTER RecordEpisode.**

**What it does:** Crystallizes session's drawer evidence into wiki page scaffolds. Returns candidate existing pages and a draft scaffold.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `wing` | ✅ | Project/namespace |
| `session_summary` | ✅ | One-paragraph summary — write as if briefing your future self |
| `page_hints` | optional | Comma-separated page paths that might be relevant |

**Key behavior:**
- Returns `candidate_pages` (existing pages with pending drawer evidence) and `new_page_scaffold` (suggested_path, suggested_title, draft_outline)
- Agent must follow up with WikiWrite to actually create/update the page
- Returns `confidence: null`

**Agent should:** Treat as mandatory housekeeping. Skipping means knowledge stays in raw drawers.

---

### 15. ListPendingUpdates

**When:** `## Pending Reviews` section in WakeUp is non-empty.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `wing` | ✅ | Wing to scope to |
| `page_path` | optional | Filter to specific page |
| `limit` | optional | Max results (default 50) |

Returns wiki pages with queued drawer evidence awaiting review.

---

### 16. DismissPendingUpdate

**When:** A queued drawer is irrelevant to the page.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `page_path` | ✅ | Page path of the pending update |
| `drawer_id` | ✅ | Drawer ID to dismiss |

---

### 17. LintWiki — *Health check*

**When:** After writing wiki pages, or periodically.

**Parameters:**

| Param | Required | Description |
|-------|----------|-------------|
| `wing` | optional | Scope to wing (default all) |

Returns findings across 7 rules: BrokenCitation (error), OrphanPage (warn), StalePage (warn), MissingCrossLink (info), GapCandidate (info), PendingUpdates (info), ContradictionCandidate (warn).

---

## Critical Rules for the Agent

### 1. Always call WakeUp first
Every session starts with `WakeUp`. Pass the user's question as `seed_query`. This activates the L2 semantic layer, episodes, and skills.

### 2. End-of-session order is fixed
`RecordEpisode` → `Distill` → `WikiWrite`. RecordEpisode captures what happened; Distill synthesizes a wiki scaffold; WikiWrite persists.

### 3. AddMemory for verbatim, WikiWrite for synthesis
- **AddMemory**: store what the user says verbatim — decisions, errors, facts
- **WikiWrite**: store what YOU synthesize from multiple sources
- Never pass your own paraphrase to AddMemory; never skip citations in WikiWrite

### 4. Check success, then decision_support
Every response: read `success` first. If `false`, read `error.code` and don't touch `result`. If `true`, read `decision_support.suggested_action`:
- `proceed` — continue as planned
- `verify` — results uncertain, double-check with a second tool
- `retry` — query didn't work, try different terms
- `ask_user` — too uncertain, surface to user

### 5. Only SearchMemories populates confidence
All other tools have `confidence: null`. Use `decision_support` only.

### 6. agreement = "single" overrides level
Even when `level: "high"` and `score > 0.80`, if `agreement: "single"` then `suggested_action: "verify"`. Always.

### 7. Use wing consistently
Always pass `wing` to scope searches. Cross-wing queries are noisy. The wing is usually the project name.

### 8. Citations are drawer IDs
When Distill or SearchMemories returns drawer IDs (16 lowercase hex chars), pass those as `citations` to WikiWrite. Citations are validated — broken IDs cause rejection.

### 9. Episodes are for learning, not logging
RecordEpisode is for non-trivial sessions where something was attempted. Don't call it for simple Q&A. The `next_time` field is the most valuable — concrete guidance for future agents.

### 10. Skills are files, not tool responses
FindSkills returns folder paths. **Read the SKILL.md file at that path** using your file-reading tools. The skill content is not returned by the MCP tool — it's a file on disk.

### 11. KG triples persist across sessions
AddTriple records facts that WakeUp shows in the `facts` field. InvalidateTriple retires them. Use for stable relationships, not transient state.

### 12. Distill is mandatory
Skipping Distill means session knowledge stays in raw drawers and is never synthesized into wiki pages. Always call Distill before ending a non-trivial session.

---

## Tool count summary

| Group | Tools | Count |
|---|---|---|
| Retrieval | WakeUp, SearchMemories, GrepExact, GetDrawer, WikiRead, WikiSearch | 6 |
| Storage | AddMemory, AddTriple, InvalidateTriple, WikiWrite | 4 |
| Episodes | RecordEpisode, FindEpisodes | 2 |
| Skills | FindSkills | 1 |
| Wiki Maintenance | Distill, ListPendingUpdates, DismissPendingUpdate, LintWiki | 4 |
| **Total** | | **17** |
