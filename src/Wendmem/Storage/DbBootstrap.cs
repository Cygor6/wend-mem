using DuckDB.NET.Data;

namespace Wendmem.Storage;

static class DbBootstrap
{
    const string AlterSql = """
        ALTER TABLE drawers ADD COLUMN IF NOT EXISTS cluster_id       INTEGER;
        ALTER TABLE drawers ADD COLUMN IF NOT EXISTS cluster_d_bar    FLOAT;
        ALTER TABLE drawers ADD COLUMN IF NOT EXISTS cluster_d_eff    FLOAT;
        ALTER TABLE drawers ADD COLUMN IF NOT EXISTS last_accessed_at TIMESTAMPTZ;
        """;

    const string AlterWithConstraintsSql = """
        ALTER TABLE drawers ADD COLUMN IF NOT EXISTS is_representative BOOLEAN DEFAULT TRUE;
        ALTER TABLE drawers ADD COLUMN IF NOT EXISTS access_count     INTEGER DEFAULT 0;
        ALTER TABLE wiki_pages ADD COLUMN IF NOT EXISTS quality_score FLOAT DEFAULT 0.0;
        ALTER TABLE triples ADD COLUMN IF NOT EXISTS source_ref TEXT;
        """;

    public static void Initialize(DuckDBConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SET checkpoint_threshold = '8MB';
            """;
        cmd.ExecuteNonQuery();

        cmd.Parameters.Clear();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS drawers (
                id                      VARCHAR PRIMARY KEY,
                wing                    VARCHAR NOT NULL,
                room                    VARCHAR NOT NULL,
                content                 VARCHAR NOT NULL,
                fts_text                VARCHAR,
                embedding_text          VARCHAR,
                parent_id               VARCHAR,
                content_hash            VARCHAR NOT NULL,
                source                  VARCHAR,
                source_mtime            BIGINT,
                importance              FLOAT NOT NULL DEFAULT 1.0,
                drawer_type             VARCHAR NOT NULL DEFAULT 'source'
                    CHECK (drawer_type IN ('source', 'synthesis')),
                mined_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
                valid_from              TIMESTAMPTZ NOT NULL DEFAULT now(),
                valid_to                TIMESTAMPTZ,
                embedding               FLOAT[512],
                is_representative       BOOLEAN NOT NULL DEFAULT TRUE,
                last_accessed_at         TIMESTAMPTZ,
                access_count             INTEGER NOT NULL DEFAULT 0,
                cluster_id              INTEGER,
                cluster_d_bar           FLOAT,
                cluster_d_eff           FLOAT
            )
            """;
        cmd.ExecuteNonQuery();

        // Migration for existing databases: add drawer_type column if missing
        cmd.Parameters.Clear();
        cmd.CommandText = """
            ALTER TABLE drawers ADD COLUMN IF NOT EXISTS
                drawer_type VARCHAR NOT NULL DEFAULT 'source'
                CHECK (drawer_type IN ('source', 'synthesis'))
            """;
        try
        { cmd.ExecuteNonQuery(); }
        catch { /* column already exists with different schema */ }

        // Migration: add cluster geometry + is_representative
        cmd.Parameters.Clear();
        cmd.CommandText = AlterSql;
        cmd.ExecuteNonQuery();

        cmd.Parameters.Clear();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS entities (
                id         VARCHAR PRIMARY KEY,
                name       VARCHAR NOT NULL,
                type       VARCHAR NOT NULL,
                properties JSON
            )
            """;
        cmd.ExecuteNonQuery();

        // KGGen dedup: canonical_name for entity deduplication
        cmd.Parameters.Clear();
        cmd.CommandText = """
            ALTER TABLE entities ADD COLUMN IF NOT EXISTS canonical_name VARCHAR
            """;
        try
        { cmd.ExecuteNonQuery(); }
        catch { }
        cmd.Parameters.Clear();
        // Backfill must mirror KnowledgeGraph.Canonicalize: lowercase, strip
        // whitespace/-/_, fold diacritics (strip_accents ≈ FoldToAscii), then
        // drop anything outside printable ASCII. The old lower(name) backfill
        // produced canonicals like "sql server" that Canonicalize ("sqlserver")
        // could never resolve, creating silent duplicates.
        cmd.CommandText = """
            UPDATE entities
            SET canonical_name = regexp_replace(
                    strip_accents(regexp_replace(lower(name), '[\s\-_]+', '', 'g')),
                    '[^\x20-\x7E]+', '', 'g')
            WHERE canonical_name IS NULL
            """;
        try
        { cmd.ExecuteNonQuery(); }
        catch { }
        cmd.Parameters.Clear();
        cmd.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS idx_entities_canonical_name
                ON entities(canonical_name)
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS triples (
                id          VARCHAR PRIMARY KEY,
                subject     VARCHAR NOT NULL,
                predicate   VARCHAR NOT NULL,
                object      VARCHAR NOT NULL,
                valid_from  DATE NOT NULL,
                valid_to    DATE,
                confidence  FLOAT NOT NULL DEFAULT 1.0,
                source_room VARCHAR,
                source_file VARCHAR,
                drawer_id   VARCHAR,
                source_ref  VARCHAR
            )
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS tunnels (
                id          VARCHAR PRIMARY KEY,
                topic       VARCHAR NOT NULL,
                wing_a      VARCHAR NOT NULL,
                room_a      VARCHAR NOT NULL,
                wing_b      VARCHAR NOT NULL,
                room_b      VARCHAR NOT NULL,
                created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS closets (
                id          VARCHAR PRIMARY KEY,
                drawer_id   VARCHAR NOT NULL,
                wing        VARCHAR,
                room        VARCHAR,
                source_file VARCHAR,
                aaak_text   VARCHAR NOT NULL,
                created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS task_memories (
                id              VARCHAR PRIMARY KEY,
                wing            VARCHAR NOT NULL,
                when_to_use     VARCHAR NOT NULL,
                content         VARCHAR NOT NULL,
                score           FLOAT NOT NULL DEFAULT 0.8,
                author          VARCHAR NOT NULL,
                keywords        VARCHAR[],
                tools_used      VARCHAR[],
                source          VARCHAR NOT NULL,
                retrieval_count INTEGER NOT NULL DEFAULT 0,
                utility_count   INTEGER NOT NULL DEFAULT 0,
                embedding       FLOAT[512],
                time_created    TIMESTAMPTZ NOT NULL DEFAULT now(),
                time_modified   TIMESTAMPTZ NOT NULL DEFAULT now(),
                last_used_at    TIMESTAMPTZ,
                CHECK (score >= 0.0 AND score <= 1.0),
                CHECK (source IN ('success', 'failure', 'comparative', 'reflection'))
            )
            """;
        cmd.ExecuteNonQuery();

        cmd.Parameters.Clear();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS tool_memories (
                wing VARCHAR NOT NULL, tool_name VARCHAR NOT NULL,
                guidelines VARCHAR NOT NULL, score FLOAT NOT NULL DEFAULT 0.5,
                author VARCHAR NOT NULL,
                time_created TIMESTAMPTZ NOT NULL DEFAULT now(),
                time_modified TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (wing, tool_name)
            )
            """;
        cmd.ExecuteNonQuery();

        cmd.Parameters.Clear();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS tool_call_history (
                id VARCHAR PRIMARY KEY, wing VARCHAR NOT NULL,
                tool_name VARCHAR NOT NULL, input_json VARCHAR, output_json VARCHAR,
                success BOOLEAN NOT NULL, score FLOAT NOT NULL DEFAULT 0.0,
                summary VARCHAR, token_cost INTEGER, time_seconds DOUBLE,
                is_summarized BOOLEAN NOT NULL DEFAULT false,
                called_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """;
        cmd.ExecuteNonQuery();

        // structured side-index for numeric/entity discrimination
        cmd.Parameters.Clear();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS drawer_tokens (
                drawer_id   VARCHAR NOT NULL,
                token_type  VARCHAR NOT NULL,
                token_value VARCHAR NOT NULL,
                PRIMARY KEY (drawer_id, token_type, token_value)
            )
            """;
        cmd.ExecuteNonQuery();

        cmd.Parameters.Clear();
        cmd.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_drawer_tokens_drawer
                ON drawer_tokens(drawer_id)
            """;
        cmd.ExecuteNonQuery();

        cmd.Parameters.Clear();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS wiki_pages (
                path        VARCHAR PRIMARY KEY,
                wing        VARCHAR NOT NULL,
                title       VARCHAR NOT NULL,
                content     VARCHAR NOT NULL,
                citations   VARCHAR[] NOT NULL DEFAULT [],
                backlinks   VARCHAR[] NOT NULL DEFAULT [],
                updated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_by  VARCHAR,
                embedding   FLOAT[512],
                quality_score FLOAT NOT NULL DEFAULT 0.0
            )
            """;
        cmd.ExecuteNonQuery();
        cmd.Parameters.Clear();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS wiki_log (
                id          VARCHAR PRIMARY KEY,
                wing        VARCHAR NOT NULL,
                event_type  VARCHAR NOT NULL,
                page_path   VARCHAR,
                summary     VARCHAR NOT NULL,
                occurred_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """;
        cmd.ExecuteNonQuery();

        // Wiki backlinks
        cmd.Parameters.Clear();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS wiki_backlinks (
                source_path VARCHAR NOT NULL,
                target_path VARCHAR NOT NULL,
                PRIMARY KEY (source_path, target_path)
            )
            """;
        cmd.ExecuteNonQuery();

        cmd.Parameters.Clear();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS wiki_pending_updates (
                id            VARCHAR PRIMARY KEY,
                wing          VARCHAR NOT NULL,
                page_path     VARCHAR NOT NULL,
                drawer_id     VARCHAR NOT NULL,
                similarity    FLOAT NOT NULL,
                queued_at     TIMESTAMPTZ DEFAULT now(),
                resolved_at   TIMESTAMPTZ,
                resolution    VARCHAR
            )
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS palace_activity (
                id          VARCHAR PRIMARY KEY,
                ts          TIMESTAMPTZ DEFAULT now(),
                wing        VARCHAR,
                action      VARCHAR NOT NULL,
                target      VARCHAR,
                agent       VARCHAR,
                summary     VARCHAR
            )
            """;
        cmd.ExecuteNonQuery();

        // Bootstrap FTS indexes if data exists but index does not.
        BootstrapFts(db, "drawers", "id", "fts_main_drawers", "fts_text");
        BootstrapFts(db, "closets", "id", "fts_main_closets", "aaak_text");
        BootstrapFts(db, "wiki_pages", "path", "fts_main_wiki_pages", "content", "title");

        // Room classification log for fail-improve loop
        cmd.Parameters.Clear();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS room_classification_log (
                id            VARCHAR PRIMARY KEY,
                source_file   VARCHAR NOT NULL,
                extension     VARCHAR NOT NULL,
                directory     VARCHAR NOT NULL,
                assigned_room VARCHAR NOT NULL,
                was_fallback  BOOLEAN NOT NULL,
                classified_at TIMESTAMPTZ DEFAULT now()
            )
            """;
        cmd.ExecuteNonQuery();

        cmd.Parameters.Clear();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS episodes (
                id            VARCHAR PRIMARY KEY,
                wing          VARCHAR NOT NULL,
                goal          VARCHAR NOT NULL,
                plan          VARCHAR,
                outcome       VARCHAR NOT NULL,
                what_worked   VARCHAR,
                what_failed   VARCHAR,
                next_time     VARCHAR,
                drawer_refs   VARCHAR,
                skill_refs    VARCHAR,
                embedding     FLOAT[512],
                started_at    TIMESTAMPTZ,
                ended_at      TIMESTAMPTZ DEFAULT now(),
                agent         VARCHAR
            )
            """;
        cmd.ExecuteNonQuery();

        cmd.Parameters.Clear();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS skills (
                id              VARCHAR PRIMARY KEY,
                wing            VARCHAR,
                name            VARCHAR NOT NULL,
                description     VARCHAR NOT NULL,
                folder_path     VARCHAR NOT NULL,
                skill_md_path   VARCHAR NOT NULL,
                skill_md_mtime  BIGINT NOT NULL,
                metadata_json   VARCHAR,
                compatibility   VARCHAR,
                license         VARCHAR,
                embedding       FLOAT[512],
                success_count   INTEGER DEFAULT 0,
                failure_count   INTEGER DEFAULT 0,
                last_used_at    TIMESTAMPTZ,
                registered_at   TIMESTAMPTZ DEFAULT now(),
                UNIQUE (folder_path)
            )
            """;
        cmd.ExecuteNonQuery();

        cmd.Parameters.Clear();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS reflection_drafts (
                id              VARCHAR PRIMARY KEY,
                wing            VARCHAR NOT NULL,
                question        VARCHAR NOT NULL,
                suggested_path  VARCHAR NOT NULL,
                suggested_title VARCHAR NOT NULL,
                draft_content   VARCHAR NOT NULL,
                citations       VARCHAR NOT NULL,
                created_at      TIMESTAMPTZ DEFAULT now(),
                status          VARCHAR DEFAULT 'pending'
            )
            """;
        cmd.ExecuteNonQuery();

        cmd.Parameters.Clear();
        // v5 = triples.source_ref. v6 = canonicalization baseline (FoldToAscii
        // canonicals + recomputed entity/triple hash ids). Fresh databases have
        // no data to migrate, so they start at 6; existing v5 databases are
        // upgraded by KnowledgeGraph.MigrateCanonicalizationAsync at startup,
        // which self-gates on this version and bumps it to 6 when done.
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);
            INSERT INTO schema_version (version) SELECT 6
                WHERE NOT EXISTS (SELECT 1 FROM schema_version)
            """;
        cmd.ExecuteNonQuery();

        cmd.Parameters.Clear();
        cmd.CommandText = AlterWithConstraintsSql;
        try
        { cmd.ExecuteNonQuery(); }
        catch { /* DuckDB may reject ALTER with constraints on existing columns */ }

        VerifyMigrations(db);
    }

    static void VerifyMigrations(DuckDBConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT column_name FROM information_schema.columns
            WHERE table_name = 'drawers'
              AND column_name IN ('access_count', 'last_accessed_at', 'cluster_id',
                                  'cluster_d_bar', 'cluster_d_eff', 'is_representative',
                                  'drawer_type')
            ORDER BY column_name
            """;
        using var reader = cmd.ExecuteReader();
        var found = new HashSet<string>();
        while (reader.Read())
            found.Add(reader.GetString(0));

        var expected = new[] { "access_count", "cluster_d_bar", "cluster_d_eff",
            "cluster_id", "drawer_type", "is_representative", "last_accessed_at" };
        foreach (var col in expected)
        {
            if (!found.Contains(col))
                throw new InvalidOperationException(
                    $"Migration verification failed: column 'drawers.{col}' not found");
        }

        cmd.Parameters.Clear();
        cmd.CommandText = """
            SELECT column_name FROM information_schema.columns
            WHERE table_name = 'entities'
              AND column_name = 'canonical_name'
            """;
        using var eReader = cmd.ExecuteReader();
        if (!eReader.Read())
            throw new InvalidOperationException(
                "Migration verification failed: column 'entities.canonical_name' not found");

        // Verify triples.source_ref exists (added in schema v5)
        cmd.Parameters.Clear();
        cmd.CommandText = """
            SELECT column_name FROM information_schema.columns
            WHERE table_name = 'triples'
              AND column_name = 'source_ref'
            """;
        using var tReader = cmd.ExecuteReader();
        if (!tReader.Read())
            throw new InvalidOperationException(
                "Migration verification failed: column 'triples.source_ref' not found");
    }

    static void BootstrapFts(DuckDBConnection db, string table, string idColumn,
        string schemaName, params string[] columns)
    {
        using var countCmd = db.CreateCommand();
        countCmd.CommandText = $"SELECT count(*) FROM {table}";
        var count = (long)countCmd.ExecuteScalar()!;

        using var ftsCmd = db.CreateCommand();
        ftsCmd.CommandText = """
            SELECT count(*) FROM information_schema.schemata
            WHERE schema_name = $schema_name
            """;
        ftsCmd.Parameters.Add(new DuckDBParameter("schema_name", schemaName));
        var ftsExists = (long)ftsCmd.ExecuteScalar()! > 0;

        if (count > 0 && !ftsExists)
        {
            var columnList = string.Join(", ", columns.Select(c => $"'{c}'"));
            using var rebuildCmd = db.CreateCommand();
            rebuildCmd.CommandText = $"""
                PRAGMA create_fts_index('{table}', '{idColumn}', {columnList},
                    stemmer = 'none', stopwords = 'none',
                    "ignore" = '([^a-zA-Z0-9_])+',
                    lower = 'true', strip_accents = 'true',
                    overwrite=1)
                """;
            rebuildCmd.ExecuteNonQuery();
        }
    }
}
