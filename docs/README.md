# Wendmem Documentation

Correct, concise documentation grounded in the actual codebase.

## Contents

| File | Description |
|------|-------------|
| [00-overview](00-overview.md) | What wendmem is, data model, architecture, tech stack |
| [01-ingestion](01-ingestion.md) | How to fill wendmem with information — mining, conversations, manual memories |
| [02-search-retrieval](02-search-retrieval.md) | How to search and retrieve data — WakeUp, SearchMemories, Grep, GetDrawer |
| [03-pruning-maintenance](03-pruning-maintenance.md) | Pruning, consolidation, cluster geometry, wing health |
| [04-wiki-knowledge-graph](04-wiki-knowledge-graph.md) | Wiki pages, KG triples, citations, when to use each |
| [05-mcp-tools](05-mcp-tools.md) | All 14 MCP tools with parameters and examples |
| [06-cli-commands](06-cli-commands.md) | All CLI commands with flags and usage |
| [07-workflows](07-workflows.md) | Optimal workflows for common tasks |
| [08-connecting-agents](08-connecting-agents.md) | How to connect AI agents to wendmem (Goose, Claude, Cursor, Zed, any MCP client) |

## What's Here vs Old Docs

The `docs/` folder contains Swedish-language guides and older documentation. This `documentation/` folder is the authoritative, code-accurate reference in English.

## Quick Start

```bash
# 1. Ingest your project
wendmem mine --root ./src --wing my-project

# 2. Start an agent session (it calls WakeUp automatically)
# 3. Search, add memories, write wiki pages via MCP tools

# 4. After changes, re-mine and prune
wendmem mine --root ./src --wing my-project
wendmem prune --wing my-project --threshold 0.97

# 5. After a session, distill what was learned
wendmem distill --wing my-project --summary "What was accomplished"
```

## The 14 MCP Tools

| Tool | Category | What it does |
|------|----------|-------------|
| WakeUp | Drawer | Get session context (synthesis + recent + semantic + pending updates) |
| SearchMemories | Drawer | Hybrid BM25 + cosine search |
| GrepExact | Drawer | Exact string or regex search |
| GetDrawer | Drawer | Read one drawer by ID |
| AddMemory | Drawer | Store a new memory |
| AddTriple | KG | Record a temporal fact |
| InvalidateTriple | KG | Retire a fact |
| WikiRead | Wiki | Read a wiki synthesis page |
| WikiWrite | Wiki | Create/update a wiki page with citations |
| WikiSearch | Wiki | Search wiki pages by relevance |
| ListPendingUpdates | Maintenance | List pages with new evidence queued |
| DismissPendingUpdate | Maintenance | Skip a pending update |
| LintWiki | Maintenance | 7-rule structured wiki quality check |
| Distill | Maintenance | Session-end filing decision + draft scaffold |

**Resource**: `palace://schema` — auto-generated document with wing info, routing keywords, and conventions.
