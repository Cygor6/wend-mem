# CLI Command Reference

All commands run as `wendmem <command> [options]`. Run `wendmem --help` for the full list.

## Drawer Operations

### mine

Recursively ingest files from a directory.

```bash
wendmem mine ./src --wing my-project
wendmem mine ./docs --wing my-project --room docs
```

| Flag | Required | Description |
|------|----------|-------------|
| positional path | Yes | File or directory to ingest |
| `--wing` | Yes | Wing namespace |
| `--room` | No | Override auto-classified room |

### mine-conversation

Ingest a conversation file or text.

```bash
wendmem mine-conversation --file chat.json --wing work
wendmem mine-conversation --text "Discussion about..." --wing personal
```

| Flag | Required | Description |
|------|----------|-------------|
| `--file` | One of | JSON conversation file |
| `--text` | One of | Plain text content |
| `--wing` | Yes | Wing namespace |

### sweep

Detect and optionally fix stale or missing files.

```bash
wendmem sweep ./src --wing my-project          # report only
wendmem sweep ./src --wing my-project --fix    # re-mine changed/missing files
```

| Flag | Required | Description |
|------|----------|-------------|
| positional path | Yes | Directory to scan |
| `--wing` | Yes | Wing namespace |
| `--fix` | No | Re-mine stale/missing files |

### search

Full-text search (BM25) from the command line.

```bash
wendmem search "DuckDB parameters" --wing work
```

| Flag | Required | Description |
|------|----------|-------------|
| positional query | Yes | Search query |
| `--wing` | No | Scope to wing |
| `--limit` | No | Max results |

### search-semantic

Cosine similarity search from the command line.

```bash
wendmem search-semantic "database schema" --wing work
```

| Flag | Required | Description |
|------|----------|-------------|
| positional query | Yes | Search query |
| `--wing` | No | Scope to wing |
| `--limit` | No | Max results |

### grep

Contextual search — temporal window around best match.

```bash
wendmem grep "error handling" --wing work
```

| Flag | Required | Description |
|------|----------|-------------|
| positional query | Yes | Query to find anchor |
| `--wing` | No | Scope to wing |
| `--room` | No | Scope to room |
| `--context` | No | Drawers before/after anchor (default 3) |

### grep-exact

Exact string or regex search over raw drawer content.

```bash
wendmem grep-exact "DrawerStorage" --wing work
wendmem grep-exact "(?i)duck" --wing work --room code --limit 20
```

| Flag | Required | Description |
|------|----------|-------------|
| positional pattern | Yes | Exact string or DuckDB RE2 regex |
| `--wing` | No | Scope to wing |
| `--room` | No | Scope to room |
| `--limit` | No | Max results (default 20) |

### prune

Geometry-aware consolidation of near-duplicate drawers.

```bash
wendmem prune --wing work --threshold 0.97
```

| Flag | Required | Description |
|------|----------|-------------|
| `--wing` | Yes | Wing to prune |
| `--threshold` | No | Cosine similarity threshold (default 0.97) |

Output: `Clusters: N, Retired: M, Kept: K`

### delete-drawer

Permanently delete a drawer by ID.

```bash
wendmem delete-drawer a3f2b1c8d4e5f607
```

### save-session

Save session state as a synthesis drawer.

```bash
wendmem save-session "Summary of what we discussed" --wing work
```

| Flag | Required | Description |
|------|----------|-------------|
| positional text | Yes | Session summary text |
| `--wing` | Yes | Wing namespace |

### wakeup-full

Full wakeup with content output (not MCP — outputs to stdout).

```bash
wendmem wakeup-full --wing work
```

## Wiki

All wiki commands under `wendmem wiki <subcommand>`.

### wiki list

List wiki pages.

```bash
wendmem wiki list --wing work
```

### wiki lint

Check wiki pages for quality issues using 7 structured lint rules.

```bash
wendmem wiki lint --wing work
wendmem wiki lint --wing work --json
```

| Flag | Required | Description |
|------|----------|-------------|
| `--wing` | No | Scope to wing (default: all) |
| `--json` | No | Output raw JSON instead of formatted table |

Rules: BrokenCitation (error), OrphanPage (warn), StalePage (warn), MissingCrossLink (info), GapCandidate (info), PendingUpdates (info), ContradictionCandidate (warn).

### wiki read

Read a wiki page.

```bash
wendmem wiki read architecture/storage
```

## Pending Updates

### pending list

List wiki pages with new drawer evidence queued for review.

```bash
wendmem pending list --wing work
wendmem pending list --wing work --page architecture/storage --limit 20
```

| Flag | Required | Description |
|------|----------|-------------|
| `--wing` | Yes | Wing to scope to |
| `--page` | No | Filter to specific page path |
| `--limit` | No | Max results (default 50) |

Output: formatted table with page_path, drawer_id, similarity, and queued_at columns.

### pending dismiss

Dismiss a pending update without applying it.

```bash
wendmem pending dismiss --page architecture/storage --drawer a3f2b1c8d4e5f607
```

| Flag | Required | Description |
|------|----------|-------------|
| `--page` | Yes | Page path of the pending update |
| `--drawer` | Yes | Drawer ID of the pending update |

## Activity

### activity

Show recent palace operations.

```bash
wendmem activity --wing work
wendmem activity --wing work --limit 10
```

| Flag | Required | Description |
|------|----------|-------------|
| `--wing` | No | Scope to wing (default: all) |
| `--limit` | No | Max entries (default 20) |

Output: formatted table with timestamp, wing, action, target, agent, and summary columns.

## Distill

### distill

Session-end filing: find candidate wiki pages and prepare a draft scaffold.

```bash
wendmem distill --wing work --summary "Refactored storage layer to use connection pooling"
wendmem distill --wing work --summary "..." --hints "architecture/storage,architecture/search"
```

| Flag | Required | Description |
|------|----------|-------------|
| `--wing` | Yes | Wing |
| `--summary` | Yes | One-paragraph summary of what was learned/decided |
| `--hints` | No | Comma-separated page paths to consider |

Output: JSON with candidate_pages, new_page_scaffold (suggested_path, suggested_title, draft_outline), and next_action. The agent then calls WikiWrite to persist.

## Knowledge Graph

### add-tunnel

Create a cross-wing tunnel.

```bash
wendmem add-tunnel --topic "database" --wing work --room code
```

### list-tunnels

List tunnels for a wing/room.

```bash
wendmem list-tunnels --wing work --room code
```

### list-tunnels-by-topic

List tunnels matching a topic.

```bash
wendmem list-tunnels-by-topic "database"
```

### kg-resolve

Resolve duplicate entities and normalize predicates across a wing's knowledge graph.

```bash
wendmem kg-resolve --wing work
```

| Flag | Required | Description |
|------|----------|-------------|
| `--wing` | Yes | Wing to resolve |

Output: `Entities merged: N, Triples redirected: M, Predicates normalized: K, Confidence updated: C (range: lo–hi, decay half-life: 180 days)`

### kg-eval

Evaluate retrieval quality by sampling KG triples and checking search recall. Useful for diagnosing misconfigured thresholds.

```bash
wendmem kg-eval --wing work
wendmem kg-eval --wing work --questions 50 --seed 42
wendmem kg-eval --wing work --json
```

| Flag | Required | Description |
|------|----------|-------------|
| `--wing` | Yes | Wing to evaluate |
| `--questions` | No | Number of synthetic questions (default 20) |
| `--seed` | No | Random seed for reproducibility |
| `--json` | No | Output raw JSON instead of formatted report |

Output (human): precision, pass/fail counts, and list of failed questions.

### skill-opt

Iteratively optimize a SKILL.md file using kg-eval as the validation signal.

```bash
wendmem skill-opt --wing work --skill SKILL.md
wendmem skill-opt --wing work --skill SKILL.md --epochs 5 --budget 5 --output SKILL.opt.md
```

| Flag | Required | Description |
|------|----------|-------------|
| `--wing` | Yes | Wing |
| `--skill` | Yes | Path to the SKILL.md file to optimize |
| `--epochs` | No | Optimization rounds (default 3) |
| `--budget` | No | Candidates per epoch (default 3) |
| `--output` | No | Output path (default `SKILL.opt.md`) |

### room-patterns

Show file extensions that fell through to fallback classification, grouped by frequency. Use to identify extensions worth adding to `MinerConfig.ExtensionToRoom`.

```bash
wendmem room-patterns
```

## Salience

### rescore

Recompute importance scores for all drawers in a wing. Use heuristic mode for speed, LLM mode for higher accuracy.

```bash
wendmem rescore --wing work
wendmem rescore --wing work --llm
wendmem rescore --wing work --llm --limit 500
```

| Flag | Required | Description |
|------|----------|-------------|
| `--wing` | Yes | Wing to rescore |
| `--llm` | No | Use LLM-based scoring instead of heuristics |
| `--limit` | No | Cap on how many drawers to rescore |

## Calibration

### calibrate

Measure and tune retrieval thresholds for a wing using sampled drawer pairs. Computes ECE, Brier score, and recommends `high`/`medium`/`can_proceed_min` thresholds.

```bash
wendmem calibrate --wing work
wendmem calibrate --wing work --samples 400 --dry-run
wendmem calibrate --wing work --write-config
```

| Flag | Required | Description |
|------|----------|-------------|
| `--wing` | Yes | Wing to calibrate |
| `--samples` | No | Number of drawer pairs to evaluate (default 200) |
| `--write-config` | No | Write recommended thresholds to `palace-config.json` |
| `--dry-run` | No | Report only — implies no `--write-config` |

Requires the ONNX embedding model to be loaded.

## Graph Visualization

### graph

Generate a self-contained interactive HTML knowledge graph for a wing. The output file uses D3.js force simulation to visualize relationships between wiki pages, knowledge-graph triples, drawers, episodes, and skills.

```bash
wendmem graph --wing work
wendmem graph --wing work --output my-graph.html
wendmem graph --wing work --no-drawers --no-episodes
wendmem graph --wing work --limit 50
```

| Flag | Required | Description |
|------|----------|-------------|
| `--wing` | Yes | Wing to visualize |
| `--output` | No | Output HTML file path (default `graph-<wing>.html`) |
| `--limit` | No | Max items per data source (default 150) |
| `--no-drawers` | No | Exclude drawer nodes |
| `--no-triples` | No | Exclude knowledge-graph triple nodes |
| `--no-episodes` | No | Exclude episode nodes |
| `--no-skills` | No | Exclude skill nodes |

Node types: wing (root), wiki, triple, drawer, episode, skill. Links are derived from wikilinks, citations, KG predicates, and source references.

## Episodes

### episode list

List recorded episodes (past task outcomes).

```bash
wendmem episode list --wing work
wendmem episode list --wing work --outcome failure --limit 10
```

| Flag | Required | Description |
|------|----------|-------------|
| `--wing` | Yes | Wing to scope to |
| `--outcome` | No | Filter: `success`, `failure`, or `partial` |
| `--limit` | No | Max results |

### episode show

Show full details of a single episode.

```bash
wendmem episode show <id>
```

### episode delete

Delete an episode by ID.

```bash
wendmem episode delete <id>
```

## Skills

### skills add

Register a skill folder (must contain a `SKILL.md`).

```bash
wendmem skills add ./my-skill --wing work
wendmem skills add ./my-skill --wing work --force
```

### skills list

List registered skills.

```bash
wendmem skills list --wing work
wendmem skills list --wing work --json
```

### skills show

Show full details of a skill.

```bash
wendmem skills show my-skill-name
wendmem skills show <id>
```

### skills update

Re-index a skill from its folder (picks up SKILL.md changes).

```bash
wendmem skills update my-skill-name
```

### skills remove

Unregister a skill.

```bash
wendmem skills remove my-skill-name --yes
wendmem skills remove <id> --force --yes
```

### skills reindex

Re-scan a directory tree and update all skills found.

```bash
wendmem skills reindex --root ./skills --wing work
```

### skills validate

Validate a skill folder without registering it.

```bash
wendmem skills validate ./my-skill
```

### skills new

Scaffold a new skill folder with a starter `SKILL.md`.

```bash
wendmem skills new my-skill
wendmem skills new my-skill --root ./skills
```

## Reflection

### reflect run

Analyze recent drawers and drafts reflection insights for wiki gaps or contradictions.

```bash
wendmem reflect run --wing work
wendmem reflect run --wing work --lookback 7 --write
```

| Flag | Required | Description |
|------|----------|-------------|
| `--wing` | Yes | Wing to analyze |
| `--lookback` | No | Days of history to consider |
| `--write` | No | Persist draft findings to storage |

### reflect drafts list

List pending reflection drafts.

```bash
wendmem reflect drafts list --wing work
wendmem reflect drafts list --wing work --status pending
```

### reflect drafts show

Show a single reflection draft.

```bash
wendmem reflect drafts show <id>
```

### reflect drafts dismiss

Dismiss a draft without acting on it.

```bash
wendmem reflect drafts dismiss <id>
```

### reflect drafts accept

Accept a draft (marks it as applied).

```bash
wendmem reflect drafts accept <id>
```

## Experience Memory

The experience subsystem stores task outcomes and distills lessons learned.

### search-task-memory

Search distilled experience memories.

```bash
wendmem search-task-memory "how to handle errors" --wing work
```

### distill-task-memory

Distill a task transcript into experience memories.

```bash
wendmem distill-task-memory session.json --wing work
```

### record-outcome

Mark a task as successful or failed.

```bash
wendmem record-outcome --success a3f2b1c8d4e5f607
```

### reflect-on-failure

Generate lessons from a failed task.

```bash
wendmem reflect-on-failure --failed <id> --wing work --success <id>
```

### prune-task-memory

Prune stale experience memories.

```bash
wendmem prune-task-memory --wing work
```

### export-task-memory / import-task-memory

Transfer experience memories between wings or instances.

```bash
wendmem export-task-memory --wing work --output experiences.json
wendmem import-task-memory experiences.json --wing personal
```

## Tool Memory

The tool memory subsystem records tool usage patterns and generates guidelines.

### record-tool-call

Record a tool invocation.

```bash
wendmem record-tool-call --wing work --tool SearchMemories
```

### summarize-tool-calls

Summarize recent tool usage.

```bash
wendmem summarize-tool-calls --wing work --tool SearchMemories
```

### get-tool-guidelines

Get auto-generated usage guidelines for a tool.

```bash
wendmem get-tool-guidelines --wing work --tool SearchMemories
```

### get-tool-statistics

Get usage statistics for a tool.

```bash
wendmem get-tool-statistics --wing work --tool SearchMemories
```

### list-tool-calls

List recent tool calls.

```bash
wendmem list-tool-calls --wing work --tool SearchMemories
```

## General

### stats

Show palace-wide statistics.

```bash
wendmem stats
```

### wings

List all wings with drawer counts.

```bash
wendmem wings
```

### serve

Start the MCP server with HTTP transport on port 5133 (instead of stdio).

```bash
wendmem serve
```
