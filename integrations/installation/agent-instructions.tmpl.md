# wendmem — {{WING}}

Active wing: **{{WING}}**  ·  Code root: `{{CODE_ROOT}}`

This is the lean project entry point. The full wendmem protocol and every tool's
parameters live in `references/tools.md` (and the installed wendmem skill).
**Load those on demand** — keep this file small and always-present, pull the
detail only when you need it.

## Every session

- **Start with** `WakeUp(wing: "{{WING}}", seedQuery: "<your current task>")`.
  `seedQuery` is required — it activates the semantic layer, past episodes, and
  skills. Without it you get only synthesis + recent drawers.
- Use `wing: "{{WING}}"` on **every** wendmem call this session. Never mix wings.
- Ground every project-specific claim in a retrieved drawer or wiki page — not
  training data. If unsure, search before you assert.
- Exact symbol / error string / hex ID → `GrepExact`.
  Concept / "how does X work" / topic → `SearchMemories`.
- Only `SearchMemories` returns `confidence`; other tools use `decision_support`.
  `agreement: single` → always verify, even at high score.
  `suggested_action: verify` → cross-check with a second tool before finalizing.

## Storing knowledge

- User states a durable fact / preference / decision → `AddMemory` (verbatim).
- Confirmed named relationship between entities → `AddTriple`
  (pair with `InvalidateTriple` when replacing an old fact).
- Synthesis reasoned across sources → `WikiWrite` with real citation drawer IDs.

## End of a non-trivial session — fixed order

1. `RecordEpisode(wing: "{{WING}}", goal, plan, outcome, whatWorked, whatFailed, nextTime)`
2. `Distill(wing: "{{WING}}", sessionSummary: "<one paragraph>")`
3. `WikiWrite(...)` if Distill returns a relevant scaffold (cited).

Skip all three for trivial Q&A or single-tool lookups.
