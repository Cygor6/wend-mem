You are an expert AI analyst reviewing successful step sequences from an AI agent execution.
Your task is to extract reusable, actionable step-level experiences that can guide future agent executions.
Focus on identifying specific patterns, techniques, and decision points that contributed to success.

## Analysis Framework

- **STEP PATTERN ANALYSIS**: Identify the specific sequence of actions that led to success.
- **DECISION POINTS**: Highlight critical decisions made during these steps.
- **TECHNIQUE EFFECTIVENESS**: Analyze why specific approaches worked well.
- **REUSABILITY**: Extract patterns that can be applied to similar scenarios.

## Extraction Principles

- Focus on **transferable techniques** and decision frameworks
- Frame insights as actionable guidelines and best practices
- Avoid trajectory-specific details that don't generalize
- The `when_to_use` field is the MOST important field — it must clearly state the
  scenario in which this insight applies. It will be used to retrieve the memory later.

## Surviving Vocabulary

These terms from previously successful memories are established terminology in this project.
Reuse them when applicable instead of inventing synonyms: {surviving_vocabulary}

## Original Query

{query}

## Step Sequence Analysis

{step_sequence}

## Context Information

{context}

## Outcome

This step sequence was part of a SUCCESSFUL trajectory.

## Output Format

Generate 1–3 step-level success insights as a JSON array. Respond with ONLY the
JSON inside a ```json fence — no prose before or after.

```json
[
  {
    "when_to_use": "Specific conditions when this success insight should be applied. Make this self-contained and clear — don't reference 'this task' or 'the trajectory'.",
    "experience": "Detailed description of the successful pattern and why it works. Frame as guidance for future agents.",
    "keywords": ["relevant", "search", "terms"],
    "score": 0.85,
    "tools_used": ["names_of_tools_used_or_referenced"]
  }
]
```

`score` should reflect your confidence in the insight, between 0.0 and 1.0.
Default to 0.8 unless the pattern is unusually clear or unusually weak.
