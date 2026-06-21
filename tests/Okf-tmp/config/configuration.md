---
type: Reference
title: Configuration
description: Configuration is bound from `appsettings.json` (see `appsettings.sample.json` for a full
tags:
- config
---

# Configuration

Configuration is bound from `appsettings.json` (see `appsettings.sample.json` for a full
template with the same sections). The options classes are designed to be bound from
configuration.

## Sections

| Section | Options class | Controls |
|---------|---------------|----------|
| `Classification:TypeVocabulary` | `TypeVocabularyOptions` | Canonical types, synonyms, match rules, fallback policy |
| `Topics` | `TopicTaxonomyOptions` | Topic taxonomy (canonical name → keywords), `MaxDepth`, `AllowFreeformTopics` |
| `Enrichment` | `EnrichmentOptions` | LLM master switch, body excerpt, concurrency, cache path |
| `Llm` | `LlmOptions` | Provider choice, endpoints, models, API keys, timeout |
| `Paths:Slug` | `PathSlugOptions` | Slugification (ASCII fold, lowercase, max length, separator) |
| `Deduplication` | `DeduplicationOptions` | Merge vs KeepAll, exact-duplicate collapse |
| `Bundle` | `BundleWriteOptions` | OKF version, headers, index/log on/off |

A malformed type vocabulary is caught as soon as `TypeCanonicalizer` is constructed
(missing `FallbackType`, a synonym without a matching canonical type, or colliding keys)
via `TypeVocabularyConfigurationException`.

## LLM providers

| Provider | `Llm:Provider` | Endpoint | Auth |
|----------|----------------|----------|------|
| **z.ai** | `ZAi` | `https://api.z.ai/api/paas/v4/` | `ZAI_API_KEY` env var or `Llm:ZAi:ApiKey` |
| **Ollama** | `Ollama` | `http://localhost:11434/v1/` | placeholder `"ollama"` |
| **llama.cpp** | `LlamaCpp` | `http://localhost:8080/v1/` | placeholder `"llamacpp"` |

See [02-llm-layer](02-llm-layer.md) for the LLM-specific sections, cache behavior, and
running with a local model.
