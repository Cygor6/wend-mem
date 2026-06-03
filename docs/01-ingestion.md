# Ingestion - How to Fill Wendmem with Information

Three ways to get data into wendmem: mine files, mine conversations, or add memories manually.

## 1. Mine Files (CLI)

The `wendmem mine` command recursively ingests all text files from a directory.

```bash
wendmem mine --root ./src --wing my-project
wendmem mine --root ./docs --wing my-project --room docs
```

### What happens

1. Recursively enumerates files, skipping binaries, lock files, generated code, and known junk directories (`bin`, `obj`, `node_modules`, `.git`, etc.)
2. For each file, checks `mtime`. If unchanged since last mine, skips it entirely.
3. Chunks text at **800 characters** with **100 character overlap**. Snaps to sentence boundaries (`.`, `\n`, `!`, `?`) when possible.
4. Each chunk becomes a **source drawer** with:
   - ID = first 16 hex chars of SHA-256(chunk text)
   - Embedding computed via EmbeddingGemma-300M (768-dim native → 512-dim Matryoshka truncation)
   - Room auto-classified from file path (e.g. `.cs` → `code`, `.md` → `docs`)
   - A3K compression stored as a closet
5. Dedup: `ON CONFLICT DO NOTHING` - identical chunks are silently skipped.
6. **Structured tokens extracted** via `EntityIndexService`: numbers, qualified names, hex literals, arities, function calls are parsed from content and stored in `drawer_tokens` for side-index search.
7. **Numeric facts extracted** via `NumericFactExtractor`: arity, version, return type, timeout, port, and config value facts are extracted via regex and stored as KG triples (fire-and-forget, never blocks mining).
8. FTS index rebuilt after batch.
9. **Pending updates queued**: After mining, new drawer embeddings are compared to wiki page embeddings. Matches above 0.55 cosine similarity are queued as pending updates in `wiki_pending_updates` (ON CONFLICT DO NOTHING — idempotent).
10. **Activity logged**: The mining operation is recorded in `palace_activity` with action `"mine"`, wing, root path, and a summary (e.g. "42 files, 67 drawers added").

### Room Classification (automatic)

File extensions map to rooms:

| Extension | Room |
|-----------|------|
| `.cs`, `.fs`, `.java`, `.py`, `.rs`, `.go`, `.ts`, `.js` | `code` |
| `.md`, `.txt`, `.rst`, `.adoc` | `docs` |
| `.json`, `.yaml`, `.yml`, `.toml`, `.config` | `config` |
| `.sql` | `sql` |
| `.css`, `.html`, `.razor` | `frontend` |
| Default | Inferred from directory name |

### Sweeping for Changes

```bash
wendmem sweep --root ./src --wing my-project          # report only
wendmem sweep --root ./src --wing my-project --fix     # re-mine stale/missing files
```

Sweep compares file mtimes against stored mtimes. Reports: missing (never mined), stale (changed since last mine), ok.

## 2. Mine Conversations (CLI)

```bash
wendmem mine-conversation --file chat.json --wing work
wendmem mine-conversation --text "Had a meeting about..." --wing personal
```

### What happens

1. If input is JSON array: parses each object's `"content"` field as a turn.
2. If input is plain text: treats the entire text as one turn.
3. Same 800/100 chunking as file mining.
4. Room is hardcoded to `"conversation"`.
5. No mtime tracking - conversation drawers are always re-ingested.
6. Structured tokens and numeric facts extracted same as file mining.
7. **Pending updates queued** and **activity logged** — same as file mining.

## 3. Add Memory (MCP Tool)

Agents add memories at runtime via the `AddMemory` tool:

```
AddMemory(
  text: "User prefers dark theme in all editors",
  wing: "personal",
  room: "preferences"
)
```

### What happens

1. Text is stored as-is (no chunking - the agent decides the content size).
2. Embedding computed. FTS text = content + wing + room.
3. Stored with `drawer_type = "source"`.
4. Structured tokens extracted via `EntityIndexService`.
5. Added to session delta for immediate FTS visibility.

### When to use

- User states a preference, fact, or instruction that should persist.
- Agent discovers information worth remembering.
- Session context that should survive across conversations.

### When NOT to use

- For large files - use `wendmem mine` instead.
- For relationships between entities - use `AddTriple` instead.
- For synthesized summaries - use `WikiWrite` instead.

## Best Practices for Ingestion

| Practice | Why |
|----------|-----|
| Use consistent wing names | Wings are namespaces - keep them stable across sessions |
| Mine source files before conversations | Gives semantic coverage before adding discussion context |
| Re-run `mine` after significant changes | Only changed files are re-processed (mtime check) |
| Run `sweep --fix` periodically | Catches files added outside normal workflows |
| Don't over-chunk manually | The 800-char chunking is tuned for EmbeddingGemma-300M |
| Use `AddMemory` for ephemeral session facts | Things the user says that don't come from files |
| Run `LintWiki` after mining sessions | Identifies pages that need updating based on new evidence |
| Review `ListPendingUpdates` periodically | New evidence may require wiki page revisions |
