# Pruning and Maintenance

## Why Pruning Exists

Over time, repeated mining and manual additions create near-duplicate drawers. Pruning consolidates these into fewer representatives, reducing noise and storage.

## GAC Prune — Geometry-Aware Consolidation

```bash
wendmem prune --wing work --threshold 0.97
```

### How it works

1. **Load** all embeddings for the wing.
2. **Cluster** via union-find at the given threshold (0.97 by default).
3. **For each cluster**, compute geometry (`cluster_d_bar`, `cluster_d_eff`).
4. **Route by regime**:

| Regime | Condition | Action |
|--------|-----------|--------|
| Tight | d̄_C < θ' | Keep one medoid, soft-retire the rest |
| Spread | d̄_C ≥ θ' | Keep `ceil(d̄/θ')` representatives via greedy farthest-first, soft-retire the rest |
| Singleton | 1 member | Keep unchanged |

5. **Soft-retire**: set `is_representative = FALSE` (not hard delete).
6. **Rebuild FTS** and recompute cluster geometry.

### Soft Retire is Reversible

Soft-retired drawers remain in the database. They are excluded from search via the `WHERE is_representative = TRUE` filter. If you need to undo a prune, you can manually set the flag back.

### Output

```
Clusters: 42, Retired: 18, Kept: 67
```

### Threshold Choice

| Threshold | θ' | Behavior |
|-----------|-----|----------|
| 0.97 | 0.03 | Conservative. Only merges near-identical drawers. Recommended for prose and code. |
| 0.95 | 0.05 | Moderate. Merges very similar drawers. |
| 0.92 | 0.08 | **Dangerous.** Below safe operating point. Causes identity collapse. |

The dedup threshold of 0.92 is mathematically broken. At low d_eff, θ-balls of radius 0.08 have vanishing overlap, so merged drawers lose identity coverage. **Always use 0.97 or higher.**

## Cluster Geometry Computation

Runs automatically as part of `RebuildFtsIndexAsync` (triggered after mining and pruning).

### What it computes

1. Group all drawers per wing.
2. Build clusters via union-find at threshold 0.90 (generous, for measurement only).
3. Per cluster:
   - `cluster_d_bar`: mean pairwise cosine distance (d̄_C)
   - `cluster_d_eff`: participation-ratio effective dimensionality — (Σλᵢ)² / Σλᵢ² from eigenvalues of the within-cluster Gram matrix
4. Write `cluster_id`, `cluster_d_bar`, `cluster_d_eff` to every drawer row.

### d_eff Estimation

Uses Jacobi eigenvalue decomposition on the n×n Gram matrix of centered embeddings. For clusters under ~200 members, this is exact and fast. For larger clusters, brute-force pairwise is still fine under 50K drawers per wing.

## Wing Health

There is no dedicated health endpoint yet, but you can inspect geometry by querying directly:

```sql
SELECT cluster_id,
       COUNT(*) as members,
       AVG(cluster_d_bar) as avg_d_bar,
       AVG(cluster_d_eff) as avg_d_eff,
       CASE WHEN AVG(cluster_d_bar) < 0.03 THEN 'tight' ELSE 'spread' END as regime
FROM drawers
WHERE wing = 'work' AND is_representative
GROUP BY cluster_id
ORDER BY avg_d_bar DESC;
```

High `avg_d_bar` relative to your θ' (0.03 at threshold 0.97) means the cluster is in the spread regime — pruning will cause identity loss.

## Maintenance Schedule

| Task | Frequency | Command |
|------|-----------|---------|
| Mine changed files | After significant edits | `wendmem mine --root ./src --wing project` |
| Sweep for missed files | Weekly or after directory restructuring | `wendmem sweep --root ./src --wing project --fix` |
| Prune | After large ingestion batches | `wendmem prune --wing project --threshold 0.97` |
| FTS rebuild | Automatic after mine/prune | Triggers `RebuildFtsIndexAsync` |
| Wiki lint | After mining or weekly | `wendmem wiki lint --wing project` |
| Review pending updates | After mining | `wendmem pending list --wing project` |
| Distill session | End of every non-trivial session | `wendmem distill --wing project --summary "..."` |

## Wiki Maintenance

### Lint — Structured Action List

```bash
wendmem wiki lint --wing work
wendmem wiki lint --wing work --json
```

Runs 7 detection rules over all wiki pages and returns a structured finding list:

| Rule | Severity | What it detects |
|------|----------|----------------|
| BrokenCitation | error | Cited drawer doesn't exist or is retired |
| OrphanPage | warn | No inbound or outbound [[wikilinks]] |
| StalePage | warn | All cited drawers are retired |
| MissingCrossLink | info | Page mentions another page's title without [[wikilink]] |
| GapCandidate | info | KG entity with >= 5 triples but no wiki page |
| PendingUpdates | info | Page has >= 3 unresolved pending updates |
| ContradictionCandidate | warn | Pending drawer with semantic overlap and conflicting numeric KG triple |

Use `--json` for machine-readable output. Each finding includes rule, severity, page_path, message, and details dict.

### Pending Updates — Review Queue

```bash
wendmem pending list --wing work
wendmem pending list --wing work --page architecture/storage
wendmem pending dismiss --page architecture/storage --drawer a3f2b1c8d4e5f607
```

After mining, new drawers that overlap semantically with existing wiki pages are queued as pending updates. Review them to decide if a page needs updating.

`pending list` shows page_path, drawer_id, similarity score, and queue timestamp. `pending dismiss` marks an update as resolved without changing the page.

### Activity Log

```bash
wendmem activity --wing work
wendmem activity --wing work --limit 10
```

Append-only log of palace operations: mine, wiki_write, prune, distill, add_triple, invalidate_triple. Useful for understanding what happened and when.

### Distill — Session-End Filing

```bash
wendmem distill --wing work --summary "Refactored storage layer to use connection pooling"
wendmem distill --wing work --summary "..." --hints "architecture/storage,architecture/search"
```

Returns candidate existing pages with pending drawer IDs, plus a draft scaffold for a new page. The agent then calls `WikiWrite` to persist. No auto-rewrites — always agent-initiated.

### Palace Schema Resource

The MCP resource `palace://schema` is auto-generated from live data. It includes:
- Wing names and room counts
- Routing keywords (which words map to which wing)
- Wiki conventions (citation requirements, naming rules)
- Workflow instructions for agents

Agents read this at session start to understand the palace structure.
