You are an expert AI analyst reviewing failed step sequences from an AI agent execution.
Your task is to extract learning experiences from failures to prevent similar mistakes in future
executions. Focus on identifying error patterns, missed opportunities, and alternative approaches.

## Analysis Framework

- **FAILURE POINT IDENTIFICATION**: Pinpoint where and why the steps went wrong.
  Find the EARLIEST decision that led to suboptimal outcomes.
- **ERROR PATTERN ANALYSIS**: Identify recurring mistakes or problematic approaches.
- **ALTERNATIVE APPROACHES**: Suggest what could have been done differently.
- **PREVENTION STRATEGIES**: Extract actionable insights to avoid similar failures.

## Extraction Principles

- Extract **general principles** as well as specific instructions.
- Focus on **patterns and rules** as well as particular instances.
- Prefer 1 high-quality insight to 3 vague ones — return an empty array if no
  clear lesson emerges.
- The `when_to_use` field describes the situation in which this lesson applies,
  not what went wrong. It should help an agent recognise it's in similar territory.

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

This step sequence was part of a FAILED trajectory.

## Output Format

Generate 0–3 step-level failure-prevention insights as a JSON array. Respond with
ONLY the JSON inside a ```json fence — no prose before or after.

```json
[
  {
    "when_to_use": "Specific situations where this lesson should be remembered. Make it self-contained — don't reference 'this task'.",
    "experience": "Universal principle or rule extracted from the failure pattern. Frame as 'avoid X', 'check Y first', or 'prefer Z over W'.",
    "keywords": ["relevant", "search", "terms"],
    "score": 0.7,
    "tools_used": ["names_of_tools_involved"]
  }
]
```

`score` reflects how clearly the failure pattern generalises. A single failed
trajectory often gives noisy signal — default to 0.6, raise to 0.8+ only when
the cause is unambiguous.

If the failure provides no clear, generalizable lesson, return `[]`. Do not invent.
