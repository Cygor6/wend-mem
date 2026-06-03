# Wendmem — Tool Reference

Full signatures for all MCP tools. Load this file when you need to
verify parameters, defaults, or constraints before calling a tool.

Confidence block is only populated by `SearchMemories`.
All other tools return `confidence: null` — use `decision_support` only.

> **Wing default:** When `wing` is omitted or null, the server substitutes
> `PalaceConfig.DefaultWing` (configured in appsettings `Palace:DefaultWing`,
> default `"work"`). Pass `wing` explicitly only when overriding the default
> for the session. Never mix wings within a session.

The complete tool surface is **17 MCP tools**. Skills, episodes, and
reflection drafts are managed via the wendmem CLI, not via MCP — only
read-side tools (`FindSkills`, `FindEpisodes`) and write-side tools that
the agent must invoke during a session (`RecordEpisode`) are exposed.

---

## Retrieval

### WakeUp
```
WakeUp(wing?: string, seedQuery?: string)
```
Returns active KG facts, wiki page index, pending reviews, drawer
layers, and (when `seedQuery` is set) episodes, skills, and reflection drafts.

- Omit `wing` for the default session (server uses `DefaultWing`).
  Pass `wing` only when the user explicitly requests a different context —
  this declares the session wing for all subsequent calls.
- `seedQuery` required to populate L2 **and** episodes **and** skills.
  Without it, only L0+L1 return and no past episodes/skills surface.
- Returns `confidence: null` — read `decision_support.summary` only.

**JSON tail keys after the `---` separator:**

| Key | Content |
|---|---|
| `pages` | Wiki page index — `{path, title}` records |
| `facts` | Active KG triples — `{subject, predicate, object}` records |
| `episodes` | Top 3 episodes matching `seedQuery` — `{id, goal, outcome, next_time}` |
| `skills` | Top 3 skills matching `seedQuery` — `{id, name, path, description, success_rate}` |
| `reflection_drafts` | Pending drafts — `{id, suggested_path, suggested_title, question}` |
| `activity` | Recent palace operations |

Skills field returns a **path on disk**. Read `SKILL.md` at that path
with your file tools — wendmem does not return skill content.

### SearchMemories
```
SearchMemories(query: string, wing?: string, room?: string, k?: int = 10)
```
Multi-stage: BM25 + cosine + KG entity lookup (RRF-fused), side-index
boost, salience boost, MMR diversification. Results are re-ranked, not
just scored.

- Raise `k` to 25 for broad surveys, lower to 3–5 for targeted lookups.
- **Only tool that populates `confidence`.**

Confidence thresholds (from source):
| score | level | reason |
|---|---|---|
| > 0.80 | `high` | `exact_match` |
| 0.40–0.80 | `medium` or `low`* | `semantic_match` |
| ≤ 0.40 | `low` | `poor_match` |

*`medium` when score > 0.60, `low` when score ≤ 0.60.
`can_proceed` is `true` when `score > 0.40` AND results exist.

### GrepExact
```
GrepExact(pattern: string, wing?: string, room?: string, k?: int = 20)
```
Exact string or RE2 regex over raw drawer content.

- `(?i)` prefix for case-insensitive match
- `can_proceed` is `true` when results exist, `false` when none found
- Returns `confidence: null`
- Invalid regex returns `error.code = "invalid_input"` immediately

Valid rooms: `security`, `config`, `database`, `api`, `ui`, `testing`,
`docs`, `devops`, `general`. Any other value returns zero silently.

RE2 restrictions: no backreferences (`\1`), no lookaheads (`(?=...)`).
These pass .NET validation but fail silently at the DuckDB layer.

### GetDrawer
```
GetDrawer(id: string)
```
Single drawer by 16-char hex ID. Read-only.

- Returns `error.code = "not_found"` if ID doesn't exist
- Returns `confidence: null` — `can_proceed: true` when found

### WikiRead
```
WikiRead(path: string)
```
Full page content plus citations.

### WikiSearch
```
WikiSearch(query: string, wing?: string, limit?: int = 10)
```
Finds pages by relevance (BM25 + semantic), not recency.
Increase `limit` for broad topics.

---

## Storage

### AddMemory
```
AddMemory(text: string, wing?: string, room?: string, source?: string)
```
Stores verbatim text as one immutable drawer. Deduplicated by content hash.

- `wing` omitted → server uses `DefaultWing`. Pass explicitly only when overriding.
- Do not paraphrase — verbatim only
- Pass `source` (file path, URL, conversation URI) when known
- `can_proceed: false` means content was rejected as duplicate or near-duplicate
  (cosine > 0.97) — check `summary` for the matched drawer ID
- Returns `confidence: null`
- Importance is computed automatically on insert (heuristic salience scorer)

### AddTriple
```
AddTriple(subject: string, predicate: string, obj: string, validFrom?: string)
```
Records a temporal relationship. Missing entities are created automatically.

- `predicate` must be `snake_case`
- `validFrom` format: `YYYY-MM-DD`. Defaults to today.
- If a conflict is detected, `suggested_action = "verify"` and
  `summary` describes the conflict
- Returns `confidence: null`

### InvalidateTriple
```
InvalidateTriple(subject: string, predicate: string, obj: string)
```
Marks a triple as no longer true (sets `valid_to` to today).
Triple stays in history. Always follow with `AddTriple` to record
the current truth.

- Returns `confidence: null`, `can_proceed: true` on success

### WikiWrite
```
WikiWrite(path: string, wing?: string, title: string, content: string, citations: string, agent?: string)
```
Creates or updates a synthesis page. Same `path` = update, not error.

- `citations`: comma-separated real drawer IDs — never empty string.
  `citations=""` silently succeeds with no runtime error — self-enforce.
- `title` in sentence case (`Wakeup design`, not `Wakeup Design`)
- `content` supports `[[other-path]]` for wiki links. Target 200–600 words.

---

## Episodes

### RecordEpisode
```
RecordEpisode(
    wing?: string,
    goal: string,
    plan: string,
    outcome: "success" | "partial" | "failure",
    whatWorked: string,
    whatFailed: string,
    nextTime: string,
    drawerRefs?: string,
    skillRefs?: string)
```
Records a verbal critique of a non-trivial task. **Call before `Distill`.**

- `outcome` must be exactly one of `success`, `partial`, `failure`
- `whatWorked` / `whatFailed` / `nextTime`: be specific — name tools,
  patterns, files, exact error messages
- `drawerRefs`: comma-separated 16-char drawer IDs touched this session
- `skillRefs`: comma-separated skill IDs used. Wendmem increments
  `success_count` or `failure_count` on each referenced skill based on
  `outcome`
- `failure` episodes get a +0.05 retrieval boost — past failures are
  more instructive than successes
- Returns `confidence: null`

**Do NOT call for:**
- Trivial Q&A (one search, one answer)
- Single-tool lookups
- Sessions without a clear goal

### FindEpisodes
```
FindEpisodes(query: string, wing?: string, outcome?: string = "any", k?: int = 3)
```
Find past episodes relevant to the current goal.

- WakeUp already returns top 3 episodes for `seedQuery` — use FindEpisodes
  only for narrower scope or outcome filtering
- `outcome`: `success` | `failure` | `any`
- Cosine match against episode embeddings, threshold 0.55
- Returns `confidence: null`

---

## Skills

### FindSkills
```
FindSkills(query: string, wing?: string, k?: int = 3)
```
Find registered skills relevant to a procedural task.

- WakeUp already returns top 3 skills for `seedQuery` — use FindSkills
  only for narrower lookups
- Returns: `{id, name, path, description, success_rate, success_count, failure_count}`
- **`path` is a folder on disk.** Read `SKILL.md` at that path with your
  file-reading tools — wendmem does not return skill content via MCP
- Cosine match against `(name + ': ' + description)` embeddings,
  threshold 0.50, boosted by `success_rate`
- Returns `confidence: null`

> Skills are registered, validated, updated, and removed via the wendmem
> CLI (`wendmem skills add | list | show | update | remove | reindex |
> validate | new`) — not via MCP tools. The agent only reads.

---

## Wiki Maintenance

### Distill
```
Distill(wing?: string, sessionSummary: string, pageHints?: string)
```
Mandatory session boundary event. Synthesizes raw drawers into wiki pages.

- `pageHints`: comma-separated page paths to steer toward existing pages
- Read `candidate_pages` in response before deciding to update or create
- Returns a scaffold: `{suggested_path, suggested_title, draft_outline}`
- Must run before `WikiWrite` in the same session
- **Order at session end: RecordEpisode → Distill → WikiWrite**

### ListPendingUpdates
```
ListPendingUpdates(wing?: string, pagePath?: string, limit?: int = 50)
```
Lists pages with queued drawer evidence awaiting review.

### DismissPendingUpdate
```
DismissPendingUpdate(pagePath: string, drawerId: string)
```
Marks drawer evidence as irrelevant for that page.

### LintWiki
```
LintWiki(wing?: string)
```
7-rule integrity check: broken citations, stale pages, orphan pages,
missing cross-links, gap candidates, pending updates, contradiction
candidates. Work through findings until empty.

| Rule | Severity | Detects |
|---|---|---|
| BrokenCitation | error | Cited drawer doesn't exist or is retired |
| OrphanPage | warn | No inbound or outbound `[[wikilinks]]` |
| StalePage | warn | All cited drawers are retired |
| MissingCrossLink | info | Page mentions another page's title without `[[wikilink]]` |
| GapCandidate | info | KG entity with ≥5 triples but no wiki page |
| PendingUpdates | info | Page has ≥3 unresolved pending updates |
| ContradictionCandidate | warn | Pending drawer with semantic overlap and conflicting numeric KG triple |

---

## Tool count

| Group | Tools | Count |
|---|---|---|
| Retrieval | WakeUp, SearchMemories, GrepExact, GetDrawer, WikiRead, WikiSearch | 6 |
| Storage | AddMemory, AddTriple, InvalidateTriple, WikiWrite | 4 |
| Episodes | RecordEpisode, FindEpisodes | 2 |
| Skills | FindSkills | 1 |
| Wiki Maintenance | Distill, ListPendingUpdates, DismissPendingUpdate, LintWiki | 4 |
| **Total** | | **17** |
