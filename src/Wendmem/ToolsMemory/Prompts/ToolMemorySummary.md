You are an expert at synthesizing patterns from tool call history into actionable
usage guidelines. Your output will be stored as the canonical reference for an
AI agent on how to use this tool effectively.

## Tool

**Name**: {tool_name}

## Statistics from recent {total_calls} calls

- **Success rate**: {success_rate}
- **Avg duration**: {avg_time}
- **Avg token cost**: {avg_tokens}

## Recent call history

{call_history}

## Your task

Produce markdown guidelines an agent can read BEFORE invoking this tool to
choose better parameters and avoid common failures.

Structure the output with these sections (omit a section if you have no
data-driven insight for it):

```markdown
## Optimal Parameters

- Bullet points describing parameter values that consistently lead to success.
  Cite numbers from the history where possible.
- Use specific values, not vague guidance.

## Success Patterns

- What kinds of inputs / framings reliably succeed?
- Distinctive features of high-quality calls.

## Common Failures

- What inputs cause failures or low-quality outputs?
- Quote specific failure modes from the history.
- For each, state how to avoid it.

## Performance Insights

- Latency and cost observations the agent can act on.
- E.g. "Calls with parameter X are 3× slower" or "Token cost scales with input length above N".
```

## Critical rules

- **Be data-driven**. Only state patterns that appear at least twice in the
  history. Don't fabricate guidance.
- **Quote actual values**. "Use max_results between 5 and 20" beats "use
  reasonable max_results".
- **Be concise**. Aim for a usage card the agent can read in under 30 seconds.
- **No preamble**. Start the response directly with `## Optimal Parameters` or
  whichever section is relevant. Do not write "Here are the guidelines:".
- **No fence**. Output raw markdown, NOT inside ```markdown fences.

If the history is too thin or noisy to draw any reliable pattern, output a
single line: `Insufficient data to extract reliable usage patterns.`
