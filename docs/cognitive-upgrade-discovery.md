# Cognitive Architecture Upgrade — Discovery Report

## AOT flags (Wendmem.csproj)
- `PublishAot=true` ✅
- `JsonSerializerIsReflectionEnabledByDefault=false` ✅
- Target: .NET 10, Exe, Optimize=true

## Database tables (from DbBootstrap.cs)
| Table | Purpose | Notes |
|---|---|---|
| `drawers` | Core verbatim chunks | Has `importance FLOAT DEFAULT 1.0` — **unused** |
| `entities` | KG entity nodes | Has `canonical_name` for dedup |
| `triples` | KG subject→predicate→object | Temporal valid_from/valid_to |
| `tunnels` | Cross-wing links | |
| `closets` | AAAK-compressed drawer text | |
| `task_memories` | Experience/reflection memory | **Predecessor to episodes** |
| `tool_memories` | Per-tool guidelines | |
| `tool_call_history` | Tool invocation log | |
| `drawer_tokens` | Structured side-index (F2) | |
| `wiki_pages` | Synthesis pages | Has `quality_score`, `embedding` |
| `wiki_log` | Wiki audit trail | |
| `wiki_backlinks` | Page→page links | |
| `wiki_pending_updates` | Queued evidence reviews | |
| `palace_activity` | Activity log | |
| `room_classification_log` | Room classifier feedback | |
| `schema_version` | Currently version 3 | |

## MCP tools (7 active, from `WithTools<T>()` in Program.cs)
| Tool | Class | Notes |
|---|---|---|
| WakeUp | DrawerTools | |
| SearchMemories | DrawerTools | |
| GetDrawer | DrawerTools | |
| AddMemory | DrawerTools | |
| GrepExact | DrawerTools | |
| AddTriple | KnowledgeGraphTools | |
| InvalidateTriple | KnowledgeGraphTools | |
| WikiRead | WikiTools | |
| WikiWrite | WikiTools | |
| WikiSearch | WikiTools | |
| ListPendingUpdates | WikiMaintenanceTools | |
| DismissPendingUpdate | WikiMaintenanceTools | |
| LintWiki | WikiMaintenanceTools | |
| Distill | WikiMaintenanceTools | |

## CLI commands (from CliDispatcher.cs)
`stats`, `wings`, `search`, `search-semantic`, `grep`, `grep-exact`, `prune`,
`save-session`, `delete-drawer`, `mine`, `mine-conversation`, `sweep`,
`wakeup-full`, `add-tunnel`, `list-tunnels`, `list-tunnels-by-topic`,
`pending list/dismiss`, `activity`, `distill`, `wiki list/lint/read`,
`search-task-memory`, `distill-task-memory`, `record-outcome`,
`reflect-on-failure`, `prune-task-memory`, `export-task-memory`,
`import-task-memory`, `record-tool-call`, `summarize-tool-calls`,
`get-tool-guidelines`, `get-tool-statistics`, `list-tool-calls`,
`kg-resolve`, `room-patterns`, `calibrate`, `serve`

## DI services (key singletons from Program.cs)
`DuckDbConnectionFactory`, `IEmbedder` (LazyEmbedder→GemmaEmbedder),
`DrawerStorage`, `ClosetStorage`, `EntityIndexService`, `KnowledgeGraph`,
`KgResolver`, `PalaceSearcher`, `WikiStorage`, `PendingUpdateService`,
`WikiLinter`, `ActivityLog`, `Sweeper`, `PalaceConfig`, `LlmService`,
`TaskMemoryStorage`, `ExperienceDistiller`, `ExperienceRetriever`,
`ExperienceRefinement`, `ToolMemoryStorage`, `ToolMemoryDistiller`,
`NumericFactExtractor`, `TopicShiftChunker`, `FileMiner`,
`ConversationMiner`, `EntityRefinementService`, `HallDetector`,
`WalLogger`, `AaakDialect`

## JsonSerializerContext files
1. `Serialization/WendmemJsonContext.cs` — core DTOs, DrawerTools/KG responses
2. `Wiki/Json/WendmemWikiJsonContext.cs` — wiki DTOs, WikiTools/WikiMaintenance responses

## Search pipeline (PalaceSearcher.cs)
- **WakeUp**: L0 (synthesis) + L1 (recent source) + L2 (semantic MMR) + KG facts + wiki index + pending updates
- **SearchMemories**: HybridSearchAsync (BM25+cosine+closet+KG RRF fusion) → side-index boost → KG predicate boost → conflict governance → backlink boost
- **Ranking**: `final_score = rrf_score + side_index_jaccard*0.15 + backlink_boost + decay/recency` — **no importance/salience weight**
- Embedding dim: **512** (not 384 as spec assumed)

## Phase status summary

### Phase 1 — Salience scoring: GREENFIELD
- `drawers.importance` column exists but is hardcoded to `1.0` on insert
- No `ImportanceScorer` service exists
- No `importance` reference in search ranking
- No `rescore` CLI command

### Phase 2 — Episodic memory: PARTIALLY BUILT (task_memories exists)
- `task_memories` table has: id, wing, when_to_use, content, score, author, keywords, tools_used, source, embedding, timestamps
- **Missing**: goal, plan, outcome, what_worked, what_failed, next_time, drawer_refs, skill_refs
- No MCP tools for task memory (ExperienceMcpTools.cs is empty stub)
- CLI tools exist but are admin-only (no MCP exposure)
- WakeUp has NO episode field in its output
- **Decision**: Create new `episodes` table per spec; migrate task_memories data

### Phase 3 — Skill library: GREENFIELD
- No `skills` table, no `SkillStorage`, no skill-related config
- `skills/` directory exists at repo root with SKILL.md folders (Anthropic format already!)
- No frontmatter parser, no skill indexing
- PalaceConfig has no `SkillsRoot` property

### Phase 4 — Reflection synthesis: GREENFIELD
- No `IHostedService` or background services (only `PalaceShutdownService`)
- No autonomous reflection loop
- No `reflection_drafts` table
- `Distill` exists but is agent-initiated, single-session; not multi-drawer autonomous reflection
