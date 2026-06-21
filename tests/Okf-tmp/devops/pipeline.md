---
type: Glossary Term
title: Pipeline
description: '```'
tags:
- devops
---

# Pipeline

```
Read → Parse → Enrich → Categorize → Classify → Dedup → AllocatePaths → BuildBundle → Validate → Write
```

Each stage is an injected interface, so the pipeline stays clean (it owns no I/O) and
every stage is testable in isolation. The same input plus the same collaborators always
produce the same result.

- **Read** (`FileSystemSourceReader`): scans the folder tree recursively, skips
  build/VCS directories, and picks up `.md/.markdown/.txt` in path order.
- **Parse** (`MarkdownSourceParser`): splits frontmatter and body, and transfers
  frontmatter fields into a `DraftConcept`.
- **Enrich** (`IConceptEnricher`): fills missing scalar fields (type/title/description)
  from the content. Default: `HeuristicConceptEnricher` (deterministic, keyword-based).
  When opted in: `CompositeConceptEnricher` chains heuristic → LLM; the LLM may override
  the heuristic's `type` (source frontmatter still wins) and fills remaining gaps.
  **Parallelized** via `Parallel.For` bounded by `Enrichment:MaxConcurrency` (default 4);
  preserves input order by writing into preallocated slots.
- **Categorize** (`ICategorizer`): folds LLM-proposed topics plus the source's folder
  segments into final `DirectorySegments`. `DefaultCategorizer` resolves against the topic
  taxonomy first, falls back to path keys, then to raw folders. Fully deterministic.
- **Classify** (`ConceptClassifier` + `TypeCanonicalizer`): canonicalizes `type` against
  the controlled vocabulary. The LLM's free text never reaches the output unmapped.
- **Dedup** (`ConceptDeduplicator`): merges concepts with the same normalized title
  within the same directory (merge or keep-all, per policy).
- **AllocatePaths** (`ConceptPathAllocator` + `Slugifier`): generates filesystem- and
  link-safe bundle-relative paths; avoids reserved names, numbers collisions.
- **BuildBundle** (`BundleBuilder`): builds the entire bundle in memory — one `.md` per
  concept, an `index.md` per directory, and a root `log.md` with provenance.
- **Validate** (`ConformanceValidator`): checks OKF SPEC §9 before files are flushed.
- **Write** (`BundleDiskWriter`): flushes the `BundleFile` list to disk.

## Determinism invariant

The LLM may *propose* `type`, `title`, `description`, `tags`, and `topics`; deterministic
code always canonicalizes `type` before writing and resolves `topics` against the
taxonomy. The LLM's free text never reaches the output unfiltered. An unreachable or
unconfigured LLM silently degrades, per document, to the heuristic result and the
pipeline carries on.

**Type precedence.** A `type` from the source frontmatter always wins. When the source
has no type, the LLM's proposal overrides the heuristic's lower-confidence one (keyword
counting); the `TypeCanonicalizer` still canonicalizes the result. Title and description
remain fill-only, so the LLM only adds them when the heuristic could not derive one. The
run report prints how many LLM calls contributed versus errored, so a fully-failing LLM is
never reported as success.

## Parallelism

`OkfPipeline` parallelizes only the enrich + categorize stage; classification, dedup, and
path allocation remain sequential (they are cheap and order-dependent). This keeps the
expensive stage (LLM round-trips when enabled) fast while preserving deterministic
output ordering.
