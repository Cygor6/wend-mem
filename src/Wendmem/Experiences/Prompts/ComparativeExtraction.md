You are an expert AI analyst comparing two attempts at the same task.
Your goal is to extract insights about WHICH approach works better and WHY.
Focus on the decision points where the trajectories diverged.

## Analysis Framework

- **DIVERGENCE POINTS**: Identify where the two trajectories made different choices.
- **EFFECTIVENESS DELTA**: Explain what made one approach better than the other.
- **CONTEXT SENSITIVITY**: Note any conditions under which the higher-scoring
  approach is preferred (or vice versa).
- **TRADE-OFFS**: If both approaches have merit, capture the trade-off
  (e.g. speed vs reliability, breadth vs precision).

## Extraction Principles

- The strongest insights compare two specific decisions, not entire trajectories.
- Frame `when_to_use` so an agent can recognise the situation BEFORE making the
  same choice — it must be the situational tag, not the verdict.
- Avoid "trajectory A did X" — always frame in transferable terms.
- Two approaches can both be "good enough" but one clearly preferred — capture
  that nuance with `score`.

## Surviving Vocabulary

These terms from previously successful memories are established terminology in this project.
Reuse them when applicable instead of inventing synonyms: {surviving_vocabulary}

## Higher-scoring trajectory (score: {higher_score})

{higher_steps}

## Lower-scoring trajectory (score: {lower_score})

{lower_steps}

## Output Format

Generate 1–2 comparative insights as a JSON array. Respond with ONLY the JSON
inside a ```json fence — no prose before or after.

```json
[
  {
    "when_to_use": "Specific situation where this comparative insight applies. State the scenario, not the verdict.",
    "experience": "Comparative analysis: 'When facing X, prefer approach A (which does Y) over approach B (which does Z) because…'. Include the trade-off if there is one.",
    "keywords": ["distinctive", "terms", "from_both_approaches"],
    "score": 0.8,
    "tools_used": ["tools_referenced_in_either_trajectory"]
  }
]
```

`score` should be 0.85+ when the divergence is clear-cut, 0.6-0.8 when
context-dependent, and below 0.5 means don't emit (return `[]`).

If the two trajectories don't actually diverge on a meaningful decision (e.g.
both succeed via essentially the same path), return `[]`.
