---
type: Glossary Term
title: Development
description: Requires the **.NET 10 SDK**. The build is strict (`TreatWarningsAsErrors=true`).
---

# Development

## Build and test

Requires the **.NET 10 SDK**. The build is strict (`TreatWarningsAsErrors=true`).

```bash
dotnet restore
dotnet build      # target: zero warnings, zero errors
dotnet test
```

## Pinned package versions

Package versions are pinned in `Directory.Build.props`, verified against nuget.org on
2026-06-20:

- `TUnit` 1.56.18
- `YamlDotNet` 18.0.0
- `Microsoft.Extensions.AI` 10.7.0
- `OpenAI` 2.11.0
- `Microsoft.Data.Sqlite` 10.0.9

## Things to know

- **NU1903** (SQLitePCLRaw bundled sqlite advisory) is intentionally suppressed; the cache
  feeds it only trusted local input (hashes and our own serialized DTOs), never untrusted
  SQL.
- **TUnit** runs on the Microsoft Testing Platform — the test projects are `Exe` and do
  *not* reference `Microsoft.NET.Test.Sdk`.
- **No embedding models** are added to wend-okf; wend-mem handles embeddings at ingestion,
  and duplicating them here would waste ~300 MB.
- The deterministic core must stay free of third-party dependencies; YAML, IO, and LLM
  code belong in `OkfStructurer.Okf` or `OkfStructurer.Llm`, not in `Core`.
