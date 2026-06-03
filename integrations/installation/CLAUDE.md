# Claude Code - Wendmem active

Wendmem MCP runs locally at `http://localhost:5133/mcp`.

- Default wing: `WING_NAMN`.
- Codebase: `CODE_ROOT` (default: `C:\dev` — override via WENDMEM_CODE_ROOT env var or `.wendmem-code-root` marker). If the session starts in a subdirectory of the codebase, treat the current project folder as the effective code root.
- Keep global context short. Use `.claude/skills/wendmem.md` for details and the hooks for WakeUp/Distill support.

## Session

Start project sessions with:

```text
WakeUp(wing: "WING_NAMN", seedQuery: "what you're working on today")
```

`seedQuery` is required for semantic matches (L2) — without it, only synthesis drawers and the 5 most recent are returned. Read `pending_updates` in the response; pages with queued evidence should be addressed during the session.

Read `palace://schema` if the MCP resource exists and you're unsure about available wings or rooms.

Before adding a memory or answering from project knowledge — verify the information doesn't already exist:

```text
GrepExact(pattern: "exact symbol or method", wing: "WING_NAMN")
SearchMemories(query: "vague concept or question", wing: "WING_NAMN")
```

Try 2–3 different phrasings before concluding the information is missing. Only save confirmed, durable information:

- `AddMemory(text: "<verbatim>", wing, room)` for facts, decisions, and preferences.
- `AddTriple(subject, predicate, obj)` for relationships.
- `InvalidateTriple(...)` immediately followed by `AddTriple(...)` when a fact is replaced.
- `WikiWrite(...)` only with real drawer citations.

End non-trivial sessions with:

```text
Distill(wing: "WING_NAMN", sessionSummary: "<one paragraph>")
```

Read `candidate_pages` in the response — high similarity (> 0.55) means the evidence is likely relevant.

## Hard rules

- Always pass `wing` on Wendmem calls that accept it.
- `AddMemory.text` is verbatim — do not paraphrase.
- Every `WikiWrite` must have real drawer citations.
- Bulk-import files with `wendmem mine` via CLI, not `AddMemory`.
- Don't create wings, rooms, or wiki pages speculatively — only when you have evidence to cite.
