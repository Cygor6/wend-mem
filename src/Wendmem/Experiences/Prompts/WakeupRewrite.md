You are helping an AI agent prepare for a task by adapting retrieved
procedural memories to the agent's current situation.

## Current query

{query}

## Retrieved memories

{retrieved_memories}

## Your task

Rewrite the retrieved memories into a single, focused briefing for the agent.

Rules:

1. **Group by relevance** — lead with the memory most relevant to the query.
   Drop memories that don't actually apply (even if they were retrieved).

2. **Adapt phrasing** — rewrite each memory's content so it directly addresses
   the agent's current situation. Replace generic placeholders with specifics
   from the query when the mapping is clear.

3. **Merge near-duplicates** — if two memories say similar things, combine them.

4. **Keep it actionable** — the briefing should read like a checklist of
   things to do or watch for, not an essay.

5. **Don't invent** — never add advice that wasn't in the retrieved memories.
   If a memory doesn't apply, drop it; don't fabricate.

## Output format

Output raw markdown — no fence, no preamble. Use this structure:

```
## Relevant past experience for this task

- **{Short situation tag}**: {adapted advice}
- **{Short situation tag}**: {adapted advice}

## Watch out for

- {failure-mode lesson, if any retrieved memory was failure-sourced}
```

Skip the "Watch out for" section if no failure or comparative memories were
retrieved. If NONE of the retrieved memories apply to the query, output a
single line: `No directly relevant past experience available.`
