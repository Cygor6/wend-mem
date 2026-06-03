# Optimal Workflows

Step-by-step workflows for getting the most out of wendmem.

## Workflow 1: Initial Setup

Starting a new project or knowledge domain.

```
1. wendmem mine --root ./src --wing project-name
   └── Ingests all source code

2. wendmem mine --root ./docs --wing project-name
   └── Ingests documentation

3. (In agent session) WakeUp(wing: "project-name", seedQuery: "project overview")
   └── Agent gets full context: synthesis pages, recent changes, semantic hits

4. (In agent session) AddTriple(subject: "project-name", predicate: "uses", obj: "postgres")
   └── Record key facts about the project

5. (In agent session) WikiWrite(path: "project-name/overview", ...)
   └── Write a synthesis page the agent can reuse in future sessions
```

## Workflow 2: Daily Session Start

Starting a new AI session on an existing project.

```
1. (Agent calls) WakeUp(wing: "work", seedQuery: "what I'm working on today")
   └── Returns: synthesis pages, recent drawers, semantic hits, KG facts, wiki index

2. (Agent reads output, decides what it needs)
   ├── If a wiki page is relevant → WikiRead(path: "topic")
   ├── If it needs specific code → SearchMemories(query: "...", room: "code")
   └── If it needs context around something → Grep(query: "...", contextWindow: 5)

3. (Agent works, using retrieved context)

4. (Agent learns something worth remembering)
   ├── New fact → AddTriple(...)
   ├── Useful memory → AddMemory(...)
   └── Synthesis worth persisting → WikiWrite(...)
```

## Workflow 3: Updating Knowledge

Files have changed, new information available.

```
1. wendmem mine --root ./src --wing project-name
   └── Only changed files are re-processed (mtime check)

2. wendmem sweep --root ./src --wing project-name --fix
   └── Catches any files mine missed (new directories, renamed files)

3. wendmem prune --wing project-name --threshold 0.97
   └── Consolidates near-duplicates from the re-ingestion

4. (Next session) WakeUp(wing: "project-name", seedQuery: "recent changes")
   └── Agent gets updated context
```

## Workflow 4: Recording User Preferences

User tells the agent something that should persist.

```
User says: "I always use PostgreSQL, never MySQL"

Agent should:
1. AddTriple(subject: "user", predicate: "prefers", obj: "postgresql")
2. AddMemory(content: "User always uses PostgreSQL, never MySQL", wing: "personal", room: "preferences")

In future sessions, WakeUp returns this fact automatically.
```

## Workflow 5: Building a Knowledge Base

Accumulating understanding of a complex system over time.

```
Session 1:
  wendmem mine --root ./project-src --wing complex-system
  WakeUp(wing: "complex-system", seedQuery: "architecture overview")
  WikiWrite(path: "complex-system/architecture", content: "...", citations: "...")

Session 2:
  WakeUp(wing: "complex-system", seedQuery: "data flow")
  WikiWrite(path: "complex-system/data-flow", content: "...", citations: "...")

Session 3:
  WakeUp(wing: "complex-system", seedQuery: "authentication")
  WikiWrite(path: "complex-system/auth", content: "...[[complex-system/data-flow]]...", citations: "...")

Session N:
  WakeUp(wing: "complex-system", seedQuery: "anything")
  └── Agent now sees all wiki pages, all KG facts, and semantic search
      across the full accumulated knowledge
```

## Workflow 6: Health Check

Diagnosing retrieval quality.

```
1. wendmem stats
   └── Check total drawers, wings, pages

2. wendmem wings
   └── Check drawer counts per wing

3. (DuckDB CLI) SELECT cluster_id, COUNT(*), AVG(cluster_d_bar)
   FROM drawers WHERE wing = 'work' AND is_representative
   GROUP BY cluster_id HAVING AVG(cluster_d_bar) > 0.03;
   └── Find spread clusters at risk of identity loss

4. wendmem search --query "something that should exist" --wing work --k 10
   └── Verify expected results appear

5. If quality is poor:
   wendmem prune --wing work --threshold 0.97
   └── Re-consolidate at safe threshold
```

## Workflow 7: Compounding Loop — Wiki Maintenance

The core compounding cycle: mine → detect → review → update.

```
1. wendmem mine --root ./src --wing project-name
   └── New drawers are mined, pending updates are queued automatically

2. wendmem pending list --wing project-name
   └── See which wiki pages have new evidence to review

3. (In agent session) LintWiki(wing: "project-name")
   └── Get structured action list — broken citations, stale pages, gaps, contradictions

4. (Agent reviews findings)
   ├── BrokenCitation → find the drawer, update or remove citation
   ├── StalePage → all sources retired → consider archiving or rewriting
   ├── GapCandidate → entity with 5+ triples, no page → consider writing one
   ├── PendingUpdates → page has 3+ new evidence items → consider updating
   ├── MissingCrossLink → add [[wikilink]] to mentioned page
   └── OrphanPage → add [[wikilinks]] from other pages

5. (Agent decides to update a page)
   WikiWrite(path: "topic", wing: "project-name", content: "...", citations: "new,drawer,ids")
   └── Pending updates for cited drawers are auto-resolved

6. (End of session)
   Distill(wing: "project-name", sessionSummary: "What was accomplished this session")
   └── Returns candidate pages + draft scaffold → agent calls WikiWrite if warranted
```

## Anti-Patterns to Avoid

| Anti-Pattern | Why it's bad | What to do instead |
|-------------|-------------|-------------------|
| Pruning at 0.92 | Causes identity collapse | Always use 0.97+ |
| Adding memories without wing | Pollutes cross-project search | Always specify a wing |
| Writing wiki pages without citations | Breaks provenance chain | Always cite source drawers |
| Mining the same files to different wings | Creates duplicate drawers with different IDs | One wing per source directory |
| Skipping WakeUp at session start | Agent has no context | Always WakeUp first |
| Using AddMemory for file content | No chunking, poor embedding quality | Use `wendmem mine` for files |
| Skipping `Distill` at session end | Knowledge accumulates in drawers but never makes it to wiki | Always call Distill before closing a non-trivial session |
| Ignoring lint findings | Broken citations and stale pages compound over time | Run LintWiki weekly or after mining |
| Dismissing all pending updates without review | New evidence is lost | Review similarity scores — high-similarity updates likely matter |
| Auto-rewriting wiki pages from lint findings | Lint surfaces issues but the LLM shouldn't decide content | Agent reviews findings, then consciously writes or skips |
