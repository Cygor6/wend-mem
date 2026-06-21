---
type: Glossary Term
title: OkfStructurer — Overview
description: OkfStructurer is a CLI application (C# / .NET 10, C# 14) that reads unstructured
---

# OkfStructurer — Overview

OkfStructurer is a CLI application (C# / .NET 10, C# 14) that reads unstructured
markdown/text files from a folder tree, enriches and categorizes the content
deterministically (with optional LLM assistance), and writes a conformant
**OKF bundle** (Open Knowledge Format) — without distorting the source facts. It is
built to prepare documents for ingestion into **wend-mem** and similar wiki-based
memory systems.

> **Design principle:** determinism first. The whole pipeline runs deterministically by
> default; the LLM layer is strictly opt-in and only *proposes* values that deterministic
> code canonicalizes before they reach the output.

## Project structure

```
wend-okf/
├── wendokf.slnx                       # Solution (modern slnx format, .NET 10)
├── Directory.Build.props              # Shared MSBuild properties + package versions
├── appsettings.json                   # Active configuration (type vocabulary, topics, LLM)
├── appsettings.sample.json            # Template with the same sections
├── src/
│   ├── OkfStructurer.Core/            # Deterministic core — NO third-party dependencies
│   │   ├── Bundle/                    #   BundleConcept, BundleFile
│   │   ├── Categorization/            #   ICategorizer, DefaultCategorizer, topic taxonomy
│   │   ├── Classification/            #   TypeCanonicalizer, ConceptClassifier, language detection
│   │   ├── Dedup/                     #   ConceptDeduplicator
│   │   ├── Documents/                 #   MarkdownDocument (frontmatter split)
│   │   ├── Enrichment/                #   IConceptEnricher, HeuristicConceptEnricher, Composite
│   │   ├── Ingest/                    #   DraftConcept, SourceDocument, ISourceReader
│   │   ├── Model/                     #   Frontmatter, Concept, Citation (pure models)
│   │   ├── Paths/                     #   Slugifier, ConceptPathAllocator
│   │   ├── Pipeline/                  #   OkfPipeline, PipelineOptions, PipelineResult
│   │   └── Validation/                #   ValidationResult
│   ├── OkfStructurer.Okf/             # OKF format I/O — the only place with YamlDotNet
│   │   ├── Ingest/                    #   FileSystemSourceReader, MarkdownSourceParser, BundleDiskWriter
│   │   └── Bundle/                    #   BundleWriter, IndexWriter, LogWriter, ConformanceValidator
│   ├── OkfStructurer.Llm/             # Optional LLM layer — Microsoft.Extensions.AI + OpenAI + SQLite
│   │   ├── IConceptProposer.cs        #   Async abstraction for LLM proposals
│   │   ├── OpenAiConceptProposer.cs   #   Works with Ollama, llama.cpp, z.ai
│   │   ├── LlmConceptEnricher.cs      #   Bridge: async proposer -> sync IConceptEnricher
│   │   ├── CachedConceptProposer.cs   #   Content-addressed cache wrapper
│   │   ├── SqliteProposalCache.cs     #   SHA-256-indexed SQLite cache
│   │   ├── ChatClientFactory.cs       #   Builds IChatClient from LlmOptions + timeout
│   │   └── LlmOptions.cs              #   Provider config (ZAi/Ollama/LlamaCpp), ResolveActive()
│   └── OkfStructurer.App/             # CLI host
│       ├── Program.cs                 #   Arg parsing, DI composition, report
│       └── ConfigurationLoader.cs     #   Binds appsettings.json to options objects
└── tests/
    ├── OkfStructurer.Core.Tests/      # TUnit — core logic, categorizer, enrichment
    ├── OkfStructurer.Okf.Tests/       # TUnit — bundle, validation, pipeline end-to-end
    └── OkfStructurer.Llm.Tests/       # TUnit — proposer parsing, cache, enricher bridge
```

## Layering

- **`OkfStructurer.Core`** — pure domain models and deterministic logic, with no external
  dependencies. Builds and runs without the LLM and without I/O.
- **`OkfStructurer.Okf`** — everything that needs YamlDotNet: serialization, concept
  writing, parsing, and disk I/O. References `Core`.
- **`OkfStructurer.Llm`** — the only place that depends on `Microsoft.Extensions.AI`,
  `OpenAI`, and `Microsoft.Data.Sqlite`. References `Core`. Can be removed without
  affecting deterministic operation.
- **`OkfStructurer.App`** — thin CLI host. Composes the pipeline from `appsettings.json`.

The layering is enforced by project references: `Core` references nothing external, so
the deterministic pipeline is fully buildable and testable in isolation. See
[01-pipeline](01-pipeline.md) for how the stages fit together and
[04-library](04-library.md) for embedding the pipeline in another host.
