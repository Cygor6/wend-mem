You are evaluating the quality of a single tool call result. The agent's
technical layer already determined whether the call returned without error —
your job is to determine whether the OUTPUT is actually USEFUL.

A call can succeed technically (no exception, status 200) but still be low
quality: irrelevant results, empty payload, hallucinated data, wrong format,
or output that doesn't match the input intent.

## The call

**Tool**: {tool_name}

**Input**:
```
{input}
```

**Output**:
```
{output}
```

## Scoring

Use a strict binary scale:

- **1.0 (high quality)**: The output is relevant, useful, and matches the
  intent of the input. An agent can productively use this output to continue
  the task.

- **0.0 (low quality)**: The output is irrelevant, empty, wrong format,
  hallucinated, or otherwise unusable. Even if no error was raised.

Do not output values between 0 and 1. Pick one or the other.

## Output format

Respond with ONLY this JSON inside a ```json fence — no prose:

```json
{
  "summary": "One sentence describing what this call did and whether it worked. ≤ 25 words.",
  "evaluation": "One sentence justifying the score. Reference specific aspects of input/output.",
  "score": 1.0
}
```

Calibration:
- Search returns 5 relevant results matching the query → 1.0
- Search returns "no results" but the query was valid → 0.0
- Calculator returns the correct number → 1.0
- Calculator returns "undefined" or wrong number → 0.0
- API call returns expected JSON shape → 1.0
- API call returns 200 but body is `null` or `{}` → 0.0
