---
type: Note
title: Using OkfStructurer as a library
description: 'The options classes are designed to be bound from configuration. The typical flow:'
---

# Using OkfStructurer as a library

The options classes are designed to be bound from configuration. The typical flow:

```csharp
using OkfStructurer.Core.Categorization;
using OkfStructurer.Core.Classification;
using OkfStructurer.Core.Enrichment;
using OkfStructurer.Core.Pipeline;

var categorizer = new DefaultCategorizer(options.Topics);
var enricher = new HeuristicConceptEnricher();   // or CompositeConceptEnricher with LLM
var pipeline = new OkfPipeline(
    sourceReader, sourceParser, enricher, categorizer,
    classifier, deduplicator, bundleBuilder);

var result = pipeline.Run(inputPath, options, DateOnly.FromDateTime(DateTime.UtcNow));
// result.Files          -> IReadOnlyList<BundleFile>
// result.Validation     -> ValidationResult (IsConformant, Warnings, Errors)
// result.ReviewItems    -> list of drafts flagged for human review
```

## Things to know

- `TypeCanonicalizer` is immutable and thread-safe after construction; instantiate it once
  and reuse it.
- `ConceptPathAllocator` holds state internally. Create a new one per bundle so that
  collision numbering (`-2`, `-3`) restarts.
- `OkfPipeline` parallelizes only the enrich + categorize stage; classification, dedup,
  and path allocation remain sequential (they are cheap and order-dependent).
- `LlmConceptEnricher` block-waits on the async proposer via `GetAwaiter().GetResult()`.
  This is safe in the CLI host (no `SynchronizationContext`). When embedding in a UI or
  ASP.NET host, replace it with an async enrichment path.
