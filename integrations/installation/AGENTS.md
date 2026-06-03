# Agents - Wendmem active

Wendmem MCP runs locally at `http://localhost:5133/mcp`.

- Default wing for this project folder: `WING_NAMN`.
- Codebase: `CODE_ROOT` (default: `C:\dev` — override via WENDMEM_CODE_ROOT env var or `.wendmem-code-root` marker). If the session starts in a subdirectory of the codebase, treat the current project folder as the effective code root.
- Keep this file short. The detailed protocol is in `.codex/skills/wendmem.md` and the installed SKILL.md; hooks inject WakeUp/Distill support.

## Session loop

1. Start project sessions with:

   ```text
   WakeUp(wing: "WING_NAMN", seedQuery: "what you're working on today")
   ```

   `seedQuery` is required for semantic matches — without it, only synthesis drawers and the 5 most recent are returned. Read `pending_updates` in the response; pages with queued evidence should be addressed during the session.

2. Read `palace://schema` if the resource exists and you're unsure about wings or rooms. Otherwise continue.

3. Before adding information or answering from project knowledge — verify it doesn't already exist:

   ```text
   GrepExact(pattern: "exact symbol or term", wing: "WING_NAMN")
   SearchMemories(query: "vague concept or question", wing: "WING_NAMN")
   ```

   Try 2–3 phrasings before concluding the information is missing. Use `WikiRead(path: "...")` to read a specific wiki page you already know about, or `WikiSearch(...)` to find pages when the WakeUp index isn't enough.

4. Only save confirmed, durable information:
   - `AddMemory(text: "<verbatim>", wing, room)` for facts, decisions, preferences.
   - `AddTriple(...)` for relationships.
   - `InvalidateTriple(...)` immediately followed by a new `AddTriple(...)` when a fact is replaced.
   - `WikiWrite(...)` only with real drawer citations.

5. End non-trivial sessions with:

   ```text
   Distill(wing: "WING_NAMN", sessionSummary: "<one paragraph>")
   ```

   Read `candidate_pages` — don't dismiss evidence without checking similarity scores. Only call `WikiWrite` if Distill shows synthesis is worth saving.

## Codex

Keep Codex Memories off when Wendmem is used:

```toml
[features]
memories = false
codex_hooks = true
```

## Hard rules

- Always pass `wing` on every Wendmem call that accepts it.
- `AddMemory.text` should be verbatim — do not paraphrase.
- Every `WikiWrite` must have real drawer citations.
- Bulk-import files with `wendmem mine` via CLI, not `AddMemory`.
- Don't create wings, rooms, or wiki pages speculatively — only when you have evidence to cite.
