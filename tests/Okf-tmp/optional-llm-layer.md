---
type: Reference
title: Optional LLM layer
description: LLM enrichment is **opt-in** and off by default. When it is on and a provider is
---

# Optional LLM layer

LLM enrichment is **opt-in** and off by default. When it is on and a provider is
configured, `LlmConceptEnricher` is chained after `HeuristicConceptEnricher`. It may
override the heuristic's proposed `type` (a source-provided `type` always wins) and
fills any title/description/topics the heuristic left blank. Results are cached in SQLite
so unchanged documents cost zero model calls on re-runs. The run report prints how many
calls contributed versus errored, so a dead endpoint or an out-of-balance account is
visible instead of silently degrading to heuristic-only output.

## Configuration

Two sections drive the LLM layer in `appsettings.json`:

```jsonc
{
  "Enrichment": {
    "LlmEnabled": false,          // master switch; false = deterministic
    "BodyExcerptLength": 2000,    // characters sent to the model
    "MaxConcurrency": 4,          // max parallel LLM calls
    "CachePath": ".okf-cache/proposals.sqlite"  // relative to the output bundle; "" = off
  },
  "Llm": {
    "Provider": "Ollama",         // ZAi | Ollama | LlamaCpp
    "RequestTimeoutMs": 30000,    // per-call timeout; silently degrades on timeout
    "Ollama": {
      "Endpoint": "http://localhost:11434/v1/",
      "ChatModel": "gemma3:4b",
      "ApiKey": "ollama"          // any non-empty string for Ollama
    },
    "ZAi": {
      "Endpoint": "https://api.z.ai/api/paas/v4/",
      "ChatModel": "glm-5-turbo",
      "ApiKey": ""                // overridden by the ZAI_API_KEY env var
    },
    "LlamaCpp": {
      "Endpoint": "http://localhost:8080/v1/",
      "ChatModel": "gemma3-4b",
      "ApiKey": "llamacpp"
    }
  }
}
```

The provider pattern mirrors wend-mem: same names, same endpoint shape, same
`ZAI_API_KEY` convention. Switch provider by changing `Llm:Provider` — no code change.

## Running with a local Gemma3

```bash
ollama pull gemma3:4b
# Set "Enrichment:LlmEnabled": true in appsettings.json
dotnet run --project src/OkfStructurer.App -- <input> <output>
```

Expected status line when connected:

```
enrichment: heuristic + LLM (provider=Ollama model=gemma3:4b key=appsettings.json Llm:Ollama:ApiKey)
cache: C:\output\.okf-cache\proposals.sqlite
```

When the server does not respond (e.g. Ollama not started), it silently degrades:

```
enrichment: heuristic only (LLM provider 'Ollama' not fully configured)
```

## Reasoning/thinking models

GLM-5 series (and other reasoning models like Qwen3 and DeepSeek-R1) spend output tokens
on an internal `reasoning_content` pass that is useless for a constrained JSON extraction
like categorization, and can exhaust the token budget before producing an answer. The
proposer therefore attaches a "disable thinking" parameter to every chat request.

Different models behind the same provider need **different parameter shapes**, so the
value is configurable per provider in `appsettings.json` via `DisableThinkingJson` rather
than hardcoded:

- **ZAi / GLM**: `{"thinking":{"type":"disabled"}}` (per
  [ZAi thinking-mode docs](https://docs.z.ai/guides/capabilities/thinking-mode))
- **Ollama** (Gemma3/Qwen3): `{"think":false}`
- **llama.cpp**: model-dependent. Nothing is sent by default; for a reasoning model such
  as Qwen3, set `{"chat_template_kwargs":{"enable_thinking":false}}`.

```json
"Llm": {
  "Provider": "LlamaCpp",
  "LlamaCpp": {
    "Endpoint": "http://localhost:8080/v1/",
    "ChatModel": "Qwen3-...",
    "DisableThinkingJson": "{\"chat_template_kwargs\":{\"enable_thinking\":false}}"
  }
}
```

When `DisableThinkingJson` is blank or unparseable, a built-in default per provider is
used (ZAi and Ollama ship with the values above; llama.cpp ships empty). This is
behavior-appropriate (categorization is judgment, not multi-step reasoning), not just a
workaround for the token-budget failure.

## Cache

`SqliteProposalCache` indexes on SHA-256 over (path + body excerpt + type vocabulary +
topic vocabulary). The key therefore changes whenever either the content or the governing
vocabularies change. The cache lives next to the output bundle, so deleting the bundle
also clears the cache. Set `CachePath` to `""` to disable caching.

## Files

| File | Role |
|------|------|
| `IConceptProposer.cs` | Async abstraction for LLM proposals |
| `OpenAiConceptProposer.cs` | Works with Ollama, llama.cpp, z.ai |
| `LlmConceptEnricher.cs` | Bridge: async proposer -> sync `IConceptEnricher` |
| `CachedConceptProposer.cs` | Content-addressed cache wrapper |
| `SqliteProposalCache.cs` | SHA-256-indexed SQLite cache |
| `ChatClientFactory.cs` | Builds `IChatClient` from `LlmOptions` + timeout |
| `LlmOptions.cs` | Provider config (ZAi/Ollama/LlamaCpp), `ResolveActive()` |

`LlmConceptEnricher` block-waits on the async proposer via `GetAwaiter().GetResult()`.
This is safe in the CLI host (no `SynchronizationContext`). When embedding in a UI or
ASP.NET host, replace it with an async enrichment path.
