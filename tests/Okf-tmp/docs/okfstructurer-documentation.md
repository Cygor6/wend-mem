---
type: Glossary Term
title: OkfStructurer Documentation
description: Correct, concise documentation grounded in the actual codebase.
tags:
- docs
---

# OkfStructurer Documentation

Correct, concise documentation grounded in the actual codebase.

## Contents

| File | Description |
|------|-------------|
| [00-overview](00-overview.md) | What OkfStructurer is, design principles, full project structure, layering |
| [01-pipeline](01-pipeline.md) | Pipeline stages in detail, the determinism invariant |
| [02-llm-layer](02-llm-layer.md) | Optional LLM layer, providers, cache, running with a local model |
| [03-configuration](03-configuration.md) | Full configuration sections reference |
| [04-library](04-library.md) | Using OkfStructurer as a library, threading and lifecycle notes |
| [05-development](05-development.md) | Build/test workflow, pinned packages, and project conventions |

## Scope

OkfStructurer is a deterministic CLI that compiles a markdown/text folder tree into a
conformant OKF bundle (Open Knowledge Format) for ingestion into wend-mem and similar
wiki-based memory systems. These docs cover the internals and configuration in depth;
the root [`README.md`](../README.md) is the high-level entry point.
