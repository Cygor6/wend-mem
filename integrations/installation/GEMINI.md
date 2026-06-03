# Gemini CLI - Wendmem active

Wendmem MCP runs locally at `http://localhost:5133/mcp`.

- Default wing: `WING_NAMN`.
- Codebase: `CODE_ROOT` (default: `C:\dev` — override via WENDMEM_CODE_ROOT env var or `.wendmem-code-root` marker). If the session starts in a subdirectory of the codebase, treat the current project folder as the effective code root.
- Keep global context short. Use `.gemini/skills/wendmem.md` for details and the hooks for WakeUp/Distill support.

## Session

Start project sessions with:

```text
WakeUp(wing: "WING_NAMN", seedQuery: "what you're working on today")
```

`seedQuery` is required for semantic matches (L2) — without it, only synthesis drawers and the 5 most recent are returned. Read `pending_updates` in the response; pages with queued evidence should be addressed during the session.

Read `palace://schema` if the MCP resource exists and you're unsure about available wings or rooms.

Before adding information or answering from project knowledge — verify it doesn't already exist:

```text
GrepExact(pattern: "exact symbol or term", wing: "WING_NAMN")
SearchMemories(query: "vague concept or question", wing: "WING_NAMN")
```

Try 2–3 phrasings before concluding the information is missing. Only save confirmed, durable information with `AddMemory`, `AddTriple`, and cited `WikiWrite`. Always run `InvalidateTriple` immediately followed by a new `AddTriple` when a fact is replaced.

End non-trivial sessions with:

```text
Distill(wing: "WING_NAMN", sessionSummary: "<one paragraph>")
```

Read `candidate_pages` in the response — don't dismiss evidence without checking similarity scores.

## Built-in memory

Gemini CLI's `SaveMemory` and `/memory add` save to `~/.gemini/GEMINI.md`.
Use Wendmem instead when the MCP server is available.

## Hard rules

- Always pass `wing` on Wendmem calls that accept it.
- `AddMemory.text` is verbatim — do not paraphrase.
- Every `WikiWrite` must have real drawer citations.
- Bulk-import files with `wendmem mine` via CLI, not `AddMemory`.
- Don't create wings, rooms, or wiki pages speculatively — only when you have evidence to cite.
