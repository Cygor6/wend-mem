---
name: wendmem
description: Personal memory palace for AI agents — search, store, and synthesize project knowledge across sessions. Use when the user says "remember", "search memory", "what do we know about X", "look up what we decided", "store this", or when starting any session on a known project. Use proactively before making any project-specific assumption. Do NOT use for general knowledge questions that need no project context.
compatibility: Requires the wendmem MCP server (.NET 10 NativeAOT). Works in any MCP-compatible agent. All tools return a structured response envelope — see Section 2. Thresholds are corpus-specific and set per wing in palace-config.json.
metadata:
  version: 4.3
  mcp-server: wendmem
  default-wing: work
---

# Wendmem — Agent Protocol

---

## Session Contract

Before starting any session, commit to these five obligations:

1. **Omit `wing` on all calls unless explicitly overriding.** The server injects the configured default (`"work"`). If the user directs you to a different wing, call `WakeUp(wing: "<other>", seedQuery)` first to declare it, then use that wing on every subsequent call this session. Never mix wings within a session.
2. **Ground every project-specific claim** in a retrieved drawer or wiki page — never training data alone.
3. **Resolve all `suggested_action: verify`** results before finalizing an answer.
4. **Call `RecordEpisode` before `Distill`** on non-trivial sessions — failures and successes both teach future agents.
5. **Call `Distill` at session end** — knowledge not distilled is knowledge lost.

---

## 1. Pick the Right Tool First

**Wrong tool choice is the most common retrieval failure.**

### What you have → which tool

| What you have                               | Tool                                              |
| ------------------------------------------- | ------------------------------------------------- |
| Exact symbol, method, error message, hex ID | `GrepExact`                                       |
| Concept, question, "how does X work", topic | `SearchMemories`                                  |
| A 16-char drawer ID from a citation         | `GetDrawer`                                       |
| Need a synthesis page on a topic            | `WikiSearch` → `WikiRead`                         |
| Past attempt at a similar task              | `FindEpisodes` (only if WakeUp didn't surface it) |
| Procedural how-to (multi-step workflow)     | `FindSkills` (only if WakeUp didn't surface it)   |

### Query character → which retrieval tool

| Query type                                                   | Tool           |
| ------------------------------------------------------------ | -------------- |
| `.cs` class, method, SQL fragment, version string            | GrepExact      |
| Config keys, namespaces, any string with dots or underscores | GrepExact      |
| "How does X relate to Y", "approach for...", architecture    | SearchMemories |

> `SearchMemories` on a symbol misses it — BM25 stems identifiers. `GrepExact` on a concept returns nothing useful.
> **Most episodes and skills are already returned by `WakeUp` when you pass `seedQuery`.** Only call `FindEpisodes` / `FindSkills` for narrower lookups.

---

## 2. Reading Tool Responses

Every tool returns this envelope. **Read in order before acting.**

```
{
  "success": true,
  "result": {},
  "confidence": {
    "level": "high | medium | low",
    "score": 0.0,
    "reason": "exact_match | semantic_match | poor_match",
    "signals": { "bm25": false, "semantic": 0.0, "kg_entity": false },
    "agreement": "full | partial | single | not_applicable"
  },
  "decision_support": {
    "can_proceed": true,
    "suggested_action": "proceed | verify | retry | ask_user",
    "summary": "One sentence — use this verbatim in your reasoning."
  },
  "error": null,
  "meta": { "tool": "ToolName", "duration_ms": 0 }
}
```

> **Only `SearchMemories` populates `confidence`.** All other tools return `confidence: null` — use `decision_support` only.
> **Thresholds** (`high`/`medium`) are corpus-specific values set in `palace-config.json` per wing. Default: high > 0.80, medium > 0.60.

### Step 1 — Check success

| success | Action                                  |
| ------- | --------------------------------------- |
| `false` | Read `error.code`. Do not use `result`. |
| `true`  | Continue.                               |

| error.code      | Action                               |
| --------------- | ------------------------------------ |
| `not_found`     | Broaden query or try alternate tool  |
| `conflict`      | Read `result`, ask user              |
| `invalid_input` | Fix the call — never retry unchanged |
| `internal`      | Retry once, then surface to user     |

### Step 2 — Act on confidence *(SearchMemories only)*

| level    | Meaning         | Action                               |
| -------- | --------------- | ------------------------------------ |
| `high`   | Strong match    | Proceed, cite `summary`              |
| `medium` | Plausible match | Flag uncertainty, consider verify    |
| `low`    | Weak match      | Do not act — search more or ask user |

`agreement` overrides `level` for the proceed/verify decision:

| agreement        | Action — regardless of level or score                              |
| ---------------- | ------------------------------------------------------------------ |
| `full`           | All three signals confirm — trust level, follow `suggested_action` |
| `partial`        | Two signals confirm — follow `suggested_action`                    |
| `single`         | **Always `verify`** — even when `level = "high"`                   |
| `not_applicable` | Ignore field                                                       |

`reason` tells you how the score was formed:

| reason           | Meaning                                |
| ---------------- | -------------------------------------- |
| `exact_match`    | Score above high threshold             |
| `semantic_match` | Embedding overlap                      |
| `poor_match`     | Score ≤ 0.40 — unlikely to be relevant |

> `can_proceed: true` when `score > can_proceed_min` AND results exist.
> Low confidence can still have `can_proceed: true` — always verify.

### Step 3 — Follow suggested_action *(all tools)*

| suggested\_action | Do this                                      |
| ----------------- | -------------------------------------------- |
| `proceed`         | Use result, continue reasoning               |
| `verify`          | Cross-check with a second tool before acting |
| `retry`           | Retry with a broader or rephrased query      |
| `ask_user`        | Too uncertain — surface ambiguity to user    |

### Step 4 — Use summary

Use `decision_support.summary` verbatim in your reasoning. Do not rephrase.

### Example — medium confidence + single agreement

```
SearchMemories("DrawerStorage MmrRerank") →
  level: "medium", score: 0.74, agreement: "single"
  suggested_action: "verify"

→ agreement is single → always verify, regardless of score.
→ GrepExact("MmrRerank", wing)
→ can_proceed: true  → proceed.
→ can_proceed: false → surface uncertainty to user.
```

> Full parameter reference: `references/tools.md`

---

## 3. Session Start

**Call `WakeUp` first — always.**

```
WakeUp(seedQuery: <user's current task>)
```

Omit `wing` — the server uses the default (`"work"`).

**Override:** If the user explicitly says they want to work in a different
wing, declare it here and keep it for the entire session:

```
WakeUp(wing: "<other>", seedQuery: <user's current task>)
```

Never switch wings mid-session. Never mix wings across calls.

`seedQuery` activates the L2 semantic layer **and** episode/skill retrieval.
Without it only L0+L1 return, and no past episodes or skills surface.
Pass the user's current task as the seed.

### What WakeUp returns

Text section followed by a JSON tail with these keys:

| Field                | What it contains                             | What to do                                                                        |
| -------------------- | -------------------------------------------- | --------------------------------------------------------------------------------- |
| L0 / L1 / L2         | Synthesis + recent + semantic drawers        | Read content                                                                      |
| `## Pending Reviews` | Wiki pages with new evidence                 | Act in maintenance pass                                                           |
| `pages`              | Wiki page index (paths + titles)             | `WikiRead` paths that match the task                                              |
| `facts`              | Active KG triples                            | Treat as ground truth                                                             |
| `episodes`           | Top 3 past episodes matching `seedQuery`     | Read `next_time` — it's prior advice for this task                                |
| `skills`             | Top 3 registered skills matching `seedQuery` | `FindSkills` returns paths; **read `SKILL.md` at each path with your file tools** |
| `reflection_drafts`  | Pending synthesis drafts                     | Review during session if relevant                                                 |

### When WakeUp returns an episode marked `failure`

Treat its `next_time` field as a hard constraint, not a suggestion. Past
failures are why episodes get a +0.05 retrieval boost over successes.

### When WakeUp returns a skill

`skills[].path` is a folder on disk. The skill content is **not** in the
response. Read `SKILL.md` at that path using your standard file-reading
tools before applying the procedure.

### Reading WakeUp's seed-match signal

WakeUp returns `confidence: null` like every non-SearchMemories tool. Whether
your `seedQuery` actually matched lives in `decision_support` — read it before
trusting any drawer as an answer:

| `suggested_action` | What it means | What to do |
| --- | --- | --- |
| `proceed` | Seed matched — the L2 semantic drawers are relevant | Use L2 as task context |
| `ask_user` | Weak / near-miss match — `summary` names the nearest context | Ask **one** clarifying question that references those near-misses; don't guess |
| `verify` | Seed matched nothing | Re-search with different terms, or tell the user project memory has nothing on this |

**L1 recent drawers are orientation — "where you left off" — never an answer to
your `seedQuery`.** When `suggested_action` is `ask_user` or `verify`, the recent
drawers are *not* relevant to the task; treating them as the answer is the single
most common WakeUp failure. A WakeUp that surfaces last session's unrelated work
is telling you it found nothing for *this* seed — it is not handing you a result.

---

## 4. Search While You Think

Wendmem is not a session-start archive. Consult it **during** reasoning.

**Ask before every non-trivial step:**
> "Is this assumption based on training data, or on how *this project* actually works?"

If uncertain — search first.

| About to say                            | Search                                            |
| --------------------------------------- | ------------------------------------------------- |
| "They probably use X pattern..."        | `SearchMemories("X pattern")`                     |
| "There should be a method for Y..."     | `GrepExact("Y")`                                  |
| "The timeout is probably 30s..."        | `GrepExact("timeout")`                            |
| "This must be documented..."            | `WikiSearch("topic")`                             |
| "That looks like FooException..."       | `GrepExact("FooException")`                       |
| Any config value or constant            | `GrepExact("key_name")`                           |
| "I've handled this before..."           | `FindEpisodes(query)` if WakeUp didn't surface it |
| "There must be a procedure for this..." | `FindSkills(query)` if WakeUp didn't surface it   |

> If this session is using a non-default wing, add `wing: "<other>"` to every call above.

**Chain searches** when results reveal new terms:

```
SearchMemories("storage architecture")
  → mentions "DrawerStorage.MmrRerank"
  → GrepExact("MmrRerank")
  → shows lambda
  → SearchMemories("MmrLambda PalaceConfig")
```

**Before any recommendation:**
> "Is there an existing decision in wendmem that confirms or contradicts my reasoning?"

---

## 5. Storage Protocol

**Memory holds durable knowledge. The live code is the source of truth for code.**

A project has two kinds of truth, and they belong in different places:

- *What the code does right now* lives in the source. It changes constantly, edited by many hands — your changes are one snapshot among many, already going stale the moment someone else commits. A future agent recovers this on demand with `GrepExact` / `SearchMemories` against the current source. **Never copy it into memory.**
- *Why the code is shaped this way* — the rule it enforces, the decision behind it, the constraint it must respect — the source cannot express, and it survives refactoring. This is what compounds across sessions. **This is what memory is for.**

A memory that surfaces next session must read as an explanation that helps an agent *understand a concept or a decision* — enough to then reason about whatever code or other work it faces. It must never read as a record of what one person typed into one file.

### The two-question gate — run before every store

1. **Durable?** "If this code is refactored next week, is the memory still true?"
   - Captures a decision, rule, constraint, rationale, or behavioral contract → **yes** → continue.
   - Captures a specific edit, line, or current implementation detail → **no** → **discard.**
2. **Not already in the code?** "Could a future agent recover this by grepping the live source?"
   - **Yes** → **discard** — let them grep; memory must not duplicate code.
   - **No** → continue.

Only content that passes **both** is eligible. Then route by type:

```
Durable fact, rule, constraint, decision, or rationale the user stated or confirmed?
  └─ Already in WakeUp or recent results?
       └─ NO  → AddMemory (+ source if known)
       └─ YES → skip

Reasoned across sources to a durable conclusion?
  └─ YES → WikiWrite (after Distill, with real citation IDs)

Confirmed a named, lasting relationship between entities?
  └─ YES → AddTriple (+ InvalidateTriple if replacing old fact)

Completed a non-trivial task (success OR failure)?
  └─ YES → RecordEpisode — the lesson, not the diff (before Distill)
```

### Store vs. discard — by example (code)

| Discard — ephemeral code snapshot                                     | Store instead — durable knowledge                                                                                                             |
| --------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| "Added `if (qty == 0) skip;` in PickService.cs line 142"              | "WMS treats zero-quantity pick lines as cancellations, not errors — they skip silently (ops rule)"                                            |
| "Refactored DrawerStorage to pool connections"                        | "DuckDB skips the shutdown CHECKPOINT under connection pooling — call CHECKPOINT explicitly or risk WAL loss"                                 |
| "Lowered the high-confidence threshold to 0.78 in palace-config.json" | "Confidence thresholds are corpus-specific, set per wing by `calibrate` — recalibrate when retrieval degrades, never hardcode a global value" |
| "Implemented the FindSkills tool today"                               | "Skills are read-only over MCP by design — registration stays CLI-only to hold the agent surface at 17 tools"                                 |
| A list of the files I touched this session                            | (put the transferable lesson in RecordEpisode `next_time` — the diff is already in version control)                                           |

The pattern: store the **why** and the **rule**, never the **where-in-code**. If a sentence only makes sense while pointing at a current line of a current file, it does not belong in memory.

### Quality gate — all four required before storing

1. **Durable** — survives a refactor (the two-question gate above). For code corpora this is the filter that matters most: code churns, many people edit it, and your latest edits are not authoritative next session.
2. **True** — from direct observation or explicit user confirmation.
3. **Non-obvious** — a future agent starting cold would not already know it.
4. **Self-contained** — readable without the surrounding session, specific enough to retrieve.

**Timing:**

- `AddMemory`, `AddTriple` — immediately when confirmed
- `RecordEpisode` — at task completion, **before** `Distill`
- `Distill` — mandatory at every non-trivial session end
- `WikiWrite` — after `Distill`, with real citation IDs

> `AddMemory` is idempotent — duplicates are rejected.
> Check `can_proceed` in response to confirm the memory was admitted.

### When to call RecordEpisode

| Situation                                         | Call?                |
| ------------------------------------------------- | -------------------- |
| Non-trivial task attempted, succeeded             | ✅ Yes                |
| Non-trivial task attempted, partial result        | ✅ Yes                |
| Non-trivial task attempted, failed                | ✅ **Especially yes** |
| Trivial Q&A (one search, one answer)              | ❌ No                 |
| Single-tool lookup (e.g. "what's in this drawer") | ❌ No                 |
| Session with no clear goal                        | ❌ No                 |

`what_failed` and `next_time` are the most valuable fields. Be specific.
Name tools, patterns, files, and exact error messages.

Capture the *transferable lesson* — why an approach worked or failed — not a
line-by-line changelog of edits. The diff lives in version control; the judgment
is what compounds. Naming a file as *where a lesson applies* is fine; recording *what you changed inside it* is not.

### When RecordEpisode references skills

Pass `skill_refs="skill_id_1,skill_id_2"` when the session applied registered
skills. Wendmem updates each skill's `success_count` or `failure_count` based
on the episode's `outcome` — well-tested skills surface higher in `FindSkills`.

---

## 6. Memory Filter — Business & Product Context

> Applies when the agent works with business logic, competitive intelligence,
> product decisions, market knowledge, or any non-code domain knowledge.

The two-question gate in Section 5 is tuned for code, where the enemy is *code
rot* — stale implementation lingering in memory. Business knowledge rots
differently. Its three enemies are **spin** (a competitor's self-description is
positioning, not fact), **volatility** (user counts, prices, and market figures
move within months), and **redundancy** (duplicate conclusions degrade
retrieval). This section is the parallel gate that filters for those.

### The five-question gate — run before storing any business fact

All five must be **YES** for storage to proceed.

1. **Still true in six months?**
   Exact user counts, prices, and market quotes change fast.
   - YES → continue.
   - NO → **do not store the figure.** Store the *relationship* instead
     ("X dominates Y"), date-stamped, without locking in the number.

2. **Does it drive a concrete decision or implementation?**
   Facts that steer no choice and shape no build have no action value in memory.
   - YES → continue.
   - NO → **discard.**

3. **Is it verifiable from a primary source?**
   A competitor's claims about itself (marketing, spin) are positioning, not
   fact. Forum complaints and unconfirmed rumors fail here too.
   - YES (primary source exists) → continue.
   - NO → **discard**, or store explicitly flagged as an unverified claim.

4. **Is it project-specific rather than general domain knowledge?**
   Professional knowledge the agent already carries (frameworks, design
   patterns, well-known market theory) is not what memory is for.
   - YES (specific to this project) → continue.
   - NO → **discard.**

5. **Is it not already stored in another form?**
   Confirm a conclusion does not duplicate an existing drawer. Redundancy
   erodes retrieval quality.
   - NO (new) → store.
   - YES (already present) → skip, or update the existing drawer via
     `InvalidateTriple` + `AddTriple` if the fact has changed.

### Store vs. discard — by example (business)

| Store — durable business knowledge | Discard — ephemeral or low-value |
| ---------------------------------- | -------------------------------- |
| Decisions and the rationale behind a product choice | Exact figures with high volatility |
| A competitor's verified features and pricing model | A competitor's own marketing claims |
| Strategic conclusions drawn across multiple sources | The process that led to the conclusion |
| Requirements that are legally mandated or industry-standard | General web-development or domain knowledge |
| Price references that set a commercial benchmark | Verbatim quotes from external sources |
| Architecture rules justified by a business decision | Technical implementation details that live in code |
| Market gaps and time-boxed opportunities (date-stamped) | Rumors and unconfirmed information |

### Store the relationship, not the snapshot

**Wrong:** "Vipps has 170,000 Swedish users as of June 2026"
**Right:** "Vipps is too small for Swedish restaurant integration now —
revisit in 2027. Keep the payment layer pluggable. (Status: June 2026)"

**Wrong:** "WEIQ costs 1,000 SEK/month"
**Right:** "WEIQ sets the price benchmark — 1,000 SEK/month + 1.45% tx fee.
Project pricing goal: beat or match this. (Verified June 2026)"

Always date-stamp business facts that can change. Write as an explanation to a
future agent, not as a data dump from a meeting.

### Business-context quality gate — all four required before storing

1. **Durable** — survives six months (the five-question gate above). For
   business corpora this is the filter that matters most: figures move, claims
   are spin, and a snapshot taken today misleads tomorrow.
2. **True** — from a primary source or explicit user confirmation, never from a
   competitor's self-description taken at face value.
3. **Non-obvious** — a future agent starting cold would not already know it from
   general domain knowledge.
4. **Self-contained** — readable without the surrounding session, date-stamped
   where the fact is time-sensitive, specific enough to retrieve.

---

## 7. Maintenance

Call when `## Pending Reviews` in WakeUp is non-empty, or after wiki edits.

```
ListPendingUpdates(pagePath?, limit?)
DismissPendingUpdate(pagePath, drawerId)
LintWiki()
```

If this session is using a non-default wing, pass `wing: "<other>"` explicitly.

`LintWiki` checks broken citations, stale pages, orphans, gaps, and
contradictions. Work through findings until empty.

Skills, episodes, and reflection drafts are managed via the wendmem CLI,
not via MCP. The agent does not register, update, or remove skills.

---

## 8. Session Checklist

**Start:**

- [ ] `WakeUp(seedQuery: <user's task>)` — always first; omit wing unless overriding; `seedQuery` required for L2 + episodes + skills
- [ ] If user directed a non-default wing: used `WakeUp(wing: "<other>", seedQuery)` and will use that wing for all calls this session
- [ ] If `skills` returned — read `SKILL.md` at each `path` with file tools
- [ ] If `episodes` returned — read `next_time` field; treat `failure` advice as a hard constraint
- [ ] Read WakeUp's `decision_support.suggested_action`: `proceed` → L2 is relevant; `ask_user` → ask one clarifying question naming the near-misses; `verify` → recent drawers are orientation only, **not** an answer — re-search or tell the user memory has nothing

**During work:**

- [ ] Check `success` before reading `result` on every response
- [ ] Only `SearchMemories` returns `confidence` — other tools: `decision_support` only
- [ ] `confidence.level = low` → never act, even if `can_proceed: true`
- [ ] `agreement = single` → always `verify`, regardless of level or score
- [ ] `suggested_action = verify` → run a second tool — no exceptions
- [ ] Before project-specific assumption → search (Section 4)
- [ ] Exact symbol/identifier → `GrepExact`; concept → `SearchMemories`
- [ ] Chain searches when results reveal new terms
- [ ] `AddMemory` / `AddTriple` immediately when the user confirms a *durable* fact — first run the two-question gate (code, Section 5) or the five-question gate (business, Section 6); never store code-edit snapshots or volatile figures

**End — session is complete when ALL are true:**

- [ ] User's question has a grounded answer with cited drawer IDs
- [ ] All `low`/`medium` confidence results used were verified
- [ ] No unresolved `suggested_action: verify` remains
- [ ] If non-trivial task attempted → `RecordEpisode(goal, plan, outcome, what_worked, what_failed, next_time, drawer_refs?, skill_refs?)` — add `wing:` only if overriding
- [ ] `Distill(sessionSummary, pageHints?)` called — add `wing:` only if overriding
- [ ] `WikiWrite` with real citation IDs if durable synthesis produced
- [ ] `LintWiki()` if wiki pages were edited
- [ ] If retrieval quality was poor (many `verify`/`retry`), run `wendmem calibrate --wing work --write-config` to recalibrate thresholds for the current corpus

> **End-of-session order is fixed:** `RecordEpisode` → `Distill` → `WikiWrite`.
> RecordEpisode captures what happened; Distill synthesizes a wiki scaffold;
> WikiWrite persists the synthesis with citations.

---

## 9. Gotchas

**Response envelope:**

- Only `SearchMemories` sets `confidence` — all other tools: `confidence: null`
- `agreement: "single"` → always verify, even when `level = "high"` or `score > 0.80`
- `can_proceed: true` ≠ high confidence — always check `level` for SearchMemories
- `summary` is pre-calibrated — use verbatim, never rephrase
- `error.code = "invalid_input"` → fix the call, never retry unchanged
- `success: false` → never read or act on `result`
- `signals` is only populated by `SearchMemories` — null for all other tools
- `WakeUp` returns `confidence: null` — its seed-match quality is in `decision_support.suggested_action`. `ask_user` / `verify` mean the recent (L1) drawers are **not** an answer to your seed, only orientation — never present them as a result

**Search:**

- `SearchMemories("System.AccessViolationException")` — BM25 tokenizes dots. Use `GrepExact("System\\.AccessViolationException")` instead
- `GrepExact(..., room: "code")` — not a valid room, returns zero silently. Valid rooms: `security`, `config`, `database`, `api`, `ui`, `testing`, `docs`, `devops`, `general`
- `GrepExact("database connection")` — too vague. Use `SearchMemories` instead
- Always pass `wing: "work"` — omitting wing degrades relevance significantly and risks cross-wing contamination. Using any other wing value is a protocol violation.

**Storage:**

- `AddMemory` does **not** filter for staleness — its dedup check catches only exact content repeats, never code-snapshot drawers that go stale within days, nor volatile business figures that go stale within months. The two-question gate (Section 5) and the five-question gate (Section 6) are the only things standing between memory and rot — self-enforce them. Store decisions, rules, and rationale; discard edit snapshots and volatile numbers.
- `WikiWrite` with `citations=""` silently succeeds — never omit real drawer IDs
- `InvalidateTriple` without a following `AddTriple` leaves a gap — always record current truth when retiring a fact
- `AddMemory` deduplicates by content hash — check `can_proceed` to confirm admission

**Session flow:**

- Skipping `RecordEpisode` on a non-trivial session loses the verbal critique that future agents would use — failures especially must be captured
- Skipping `Distill` leaves knowledge in raw drawers, never synthesized
- `WakeUp` pending section is `## Pending Reviews` — not `pending_updates`
- Never call `WikiWrite` without running `Distill` first in the same session
- End-of-session order is **RecordEpisode → Distill → WikiWrite** — not reversed

**Wing:**

- Omit `wing` on all calls — the server resolves the default (`"work"`)
- Override trigger: user says "use wing X" / "work on project Y" / "my personal notes" → call `WakeUp(wing: "X", seedQuery)` immediately and use `wing: "X"` on every call for the rest of this session
- Never mix wings mid-session — if you realize you called the wrong wing, restart with a correct `WakeUp`
- Never infer a wing from context — only switch on explicit user instruction
- If a tool returns `not_found` and content should exist, check wing: `wendmem wiki list --wing work` and `wendmem wings` from the CLI

**Episodes:**

- WakeUp returns up to 3 episodes when `seedQuery` is set. `FindEpisodes` is for narrower scope or outcome filtering — don't double-fetch what WakeUp already gave
- `outcome` must be exactly one of `success`, `partial`, `failure`
- `RecordEpisode` for trivial Q&A pollutes future retrieval — only call on sessions with a clear goal and concrete `next_time` advice

**Skills:**

- `FindSkills` returns folder paths, not skill content. **Read `SKILL.md` at the returned path** with your file-reading tools — wendmem does not expose skill text via MCP
- Skill `success_count` / `failure_count` updates only when `RecordEpisode` is called with `skill_refs` pointing to the skills used
- Skills are registered, validated, and removed via the wendmem CLI (`wendmem skills add | reindex | remove`) — not via MCP tools

**GrepExact RE2:**

- Backreferences (`\1`) and lookaheads (`(?=...)`) pass .NET validation but fail silently at the DuckDB RE2 layer — use RE2-safe syntax only
