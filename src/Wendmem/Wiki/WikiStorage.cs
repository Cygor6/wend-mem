using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Wendmem.Services;
using Wendmem.Storage;
using Wendmem.Wiki.Models;

namespace Wendmem.Wiki;

public sealed partial class WikiStorage
{
    readonly DuckDbConnectionFactory _dbFactory;
    readonly IEmbedder _embedder;
    readonly ILogger<WikiStorage> _logger;
    readonly ActivityLog? _activityLog;
    readonly PendingUpdateService? _pendingUpdateService;
    readonly int _embeddingDim;
    readonly IMemoryCache _cache;

    public WikiStorage(DuckDbConnectionFactory dbFactory, IEmbedder embedder, ILogger<WikiStorage> logger,
        IMemoryCache cache, ActivityLog? activityLog = null, PendingUpdateService? pendingUpdateService = null)
    {
        _dbFactory = dbFactory;
        _embedder = embedder;
        _logger = logger;
        _cache = cache;
        _activityLog = activityLog;
        _pendingUpdateService = pendingUpdateService;
        _embeddingDim = embedder.EmbeddingDimension;
    }

    public async Task<WikiPage?> ReadAsync(string path, CancellationToken ct = default)
    {
        path = PathValidator.ValidatePath(path);

        using var ro = _dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT path, wing, title, content, citations, backlinks, updated_at, updated_by
            FROM wiki_pages
            WHERE path = $path
            LIMIT 1
            """;
        cmd.Parameters.Add(new DuckDBParameter("path", path));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new WikiPage(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ReadStringArray(reader, 4),
            ReadStringArray(reader, 5),
            new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    public async Task<WriteResult> WriteAsync(
        string path, string wing, string title, string content,
        IReadOnlyList<string> citations, string? updatedBy,
        CancellationToken ct = default)
    {
        path = PathValidator.ValidatePath(path);
        wing = PathValidator.ValidateWing(wing);
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("title required");
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("content required");

        await ValidateCitationsAsync(citations, ct);

        var embedding = await _embedder.EmbedDocumentAsync(content, ct);
        var embLit = EmbeddingUtils.ToFloatArrayLiteral(embedding);

        bool exists = await PathExistsAsync(path, ct);

        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            var citParams = new List<string>(citations.Count);
            for (int i = 0; i < citations.Count; i++)
            {
                var name = $"c{i}";
                citParams.Add($"${name}");
                cmd.Parameters.Add(new DuckDBParameter(name, citations[i]));
            }
            var citLit = citations.Count == 0 ? "[]" : $"[{string.Join(",", citParams)}]";
            cmd.CommandText = $"""
                INSERT INTO wiki_pages (path, wing, title, content, citations, updated_at, updated_by, embedding, quality_score)
                VALUES ($path, $wing, $title, $content, {citLit}, now(), $updated_by, {embLit}::FLOAT[{_embeddingDim}], $quality)
                ON CONFLICT (path) DO UPDATE SET
                    wing = EXCLUDED.wing,
                    title = EXCLUDED.title,
                    content = EXCLUDED.content,
                    citations = EXCLUDED.citations,
                    updated_at = now(),
                    updated_by = EXCLUDED.updated_by,
                    embedding = EXCLUDED.embedding,
                    quality_score = EXCLUDED.quality_score
                """;
            cmd.Parameters.Add(new DuckDBParameter("path", path));
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
            cmd.Parameters.Add(new DuckDBParameter("title", title));
            cmd.Parameters.Add(new DuckDBParameter("content", content));
            cmd.Parameters.Add(new DuckDBParameter("updated_by", (object?)updatedBy ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("quality", ComputeWikiQuality(content, citations, path)));

            await cmd.ExecuteNonQueryAsync(ct);

            // Parse [[wikilinks]] and update wiki_backlinks
            using var delBlCmd = db.CreateCommand();
            delBlCmd.CommandText = "DELETE FROM wiki_backlinks WHERE source_path = $path";
            delBlCmd.Parameters.Add(new DuckDBParameter("path", path));
            await delBlCmd.ExecuteNonQueryAsync(ct);

            foreach (Match m in WikilinkRegex().Matches(content))
            {
                var target = m.Groups[1].Value;
                using var insBlCmd = db.CreateCommand();
                insBlCmd.CommandText = """
                    INSERT INTO wiki_backlinks (source_path, target_path)
                    VALUES ($source, $target)
                    ON CONFLICT DO NOTHING
                    """;
                insBlCmd.Parameters.Add(new DuckDBParameter("source", path));
                insBlCmd.Parameters.Add(new DuckDBParameter("target", target));
                await insBlCmd.ExecuteNonQueryAsync(ct);
            }
        }, ct);

        await AppendLogAsync(
            wing,
            exists ? "update" : "create",
            path,
            $"{(exists ? "Updated" : "Created")} {path} ({citations.Count} citations)",
            ct);

        _logger.LogInformation("{Op} wiki page {Path} ({CitationCount} citations)",
            exists ? "Updated" : "Created", path, citations.Count);

        if (_pendingUpdateService is not null && citations.Count > 0)
            await _pendingUpdateService.ResolveForCitationsAsync(path, citations, "updated", ct);

        if (_activityLog is not null)
            await _activityLog.LogAsync("wiki_write", wing, path, updatedBy,
                $"{(exists ? "Updated" : "Created")} {path} ({citations.Count} citations)", ct);

        _cache.Remove($"wiki-index:{wing}");
        _cache.Remove("wiki-index:");

        return exists ? WriteResult.Updated : WriteResult.Created;
    }

    public async Task<IReadOnlyList<WikiPageHeader>> IndexAsync(
        string? wing, CancellationToken ct = default)
    {
        var cacheKey = $"wiki-index:{wing ?? ""}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<WikiPageHeader>? cached))
            return cached!;

        using var ro = _dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = wing is null
            ? """
              SELECT path, wing, title, updated_at, array_length(citations) AS cc
              FROM wiki_pages
              ORDER BY updated_at DESC
              LIMIT 100
              """
            : """
              SELECT path, wing, title, updated_at, array_length(citations) AS cc
              FROM wiki_pages
              WHERE wing = $wing
              ORDER BY updated_at DESC
              LIMIT 100
              """;
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));

        var list = new List<WikiPageHeader>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new WikiPageHeader(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
                reader.IsDBNull(4) ? 0 : (int)reader.GetInt64(4)));
        }

        _cache.Set(cacheKey, (IReadOnlyList<WikiPageHeader>)list, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(5),
            Size = 1
        });
        return list;
    }

    public async Task<IReadOnlyList<WikiPageHeader>> SearchAsync(
        string query, string? wing, int limit, CancellationToken ct = default)
    {
        var queryEmbedding = await _embedder.EmbedQueryAsync(query, ct);
        var qLit = EmbeddingUtils.ToFloatArrayLiteral(queryEmbedding);

        try
        {
            return await ExecuteSearchAsync(query, wing, limit, qLit, ct);
        }
        catch (DuckDB.NET.Data.DuckDBException ex) when (ex.Message.Contains("match_bm25"))
        {
            // FTS index was never built (e.g. wiki_pages was empty at startup so BootstrapFts
            // skipped it, and WriteAsync doesn't rebuild it). Rebuild now and retry once.
            _logger.LogInformation("FTS index for wiki_pages is missing; rebuilding before retrying search.");
            await RebuildFtsAsync(ct);
            return await ExecuteSearchAsync(query, wing, limit, qLit, ct);
        }
    }

    private async Task<IReadOnlyList<WikiPageHeader>> ExecuteSearchAsync(
        string query, string? wing, int limit, string qLit, CancellationToken ct)
    {
        using var ro = _dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            WITH backlink_counts AS (
                SELECT target_path, COUNT(*) AS inbound_links
                FROM wiki_backlinks
                GROUP BY target_path
            ),
            scored AS (
                SELECT
                    w.path,
                    w.title,
                    w.wing,
                    w.updated_at,
                    array_length(w.citations) AS cc,
                    fts_main_wiki_pages.match_bm25(w.path, $query, fields := 'content') AS bm25,
                    array_cosine_similarity(w.embedding, {qLit}::FLOAT[{_embeddingDim}]) AS sem,
                    COALESCE(b.inbound_links, 0) AS inbound
                FROM wiki_pages w
                LEFT JOIN backlink_counts b ON b.target_path = w.path
                WHERE ($wing IS NULL OR w.wing = $wing)
            )
            SELECT path, title, wing, updated_at, cc,
                (0.40 * COALESCE(bm25, 0)
              + 0.40 * COALESCE(sem,  0)
              + 0.20 * log(1.0 + inbound)) AS score
            FROM scored
            WHERE bm25 IS NOT NULL OR sem > 0.3
            ORDER BY score DESC
            LIMIT $limit
            """;
        cmd.Parameters.Add(new DuckDBParameter("query", query));
        cmd.Parameters.Add(new DuckDBParameter("wing", wing is not null ? wing : (object)DBNull.Value));
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));

        var list = new List<WikiPageHeader>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new WikiPageHeader(
                reader.GetString(0),
                reader.GetString(2), // wing is at index 2
                reader.GetString(1), // title is at index 1
                new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
                reader.IsDBNull(4) ? 0 : (int)reader.GetInt64(4),
                reader.IsDBNull(5) ? 0f : (float)reader.GetDouble(5)));
        }
        return list;
    }

    public async Task<LintResult> LintAsync(
        string? wing, CancellationToken ct = default)
    {
        await RebuildBacklinksAsync(wing, ct);
        var orphans = await FindOrphansAsync(wing, ct);
        var broken = await FindBrokenCitationsAsync(wing, ct);
        var stale = await FindStalePagesAsync(wing, ct);
        await RebuildFtsAsync(ct);

        await AppendLogAsync(
            wing ?? "all",
            "lint",
            null,
            $"Lint: {orphans.Count} orphans, {broken.Count} broken citations, {stale.Count} stale",
            ct);

        _logger.LogInformation("Wiki lint completed: {Orphans} orphans, {Broken} broken, {Stale} stale",
            orphans.Count, broken.Count, stale.Count);

        return new LintResult(orphans, broken, stale);
    }

    public async Task<IReadOnlyList<WikiLogEntry>> LogAsync(
        string? wing, int limit, CancellationToken ct = default)
    {
        using var ro = _dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = wing is null
            ? "SELECT id, wing, event_type, page_path, summary, occurred_at FROM wiki_log ORDER BY occurred_at DESC LIMIT $limit"
            : "SELECT id, wing, event_type, page_path, summary, occurred_at FROM wiki_log WHERE wing = $wing ORDER BY occurred_at DESC LIMIT $limit";
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));

        var list = new List<WikiLogEntry>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new WikiLogEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero)));
        }
        return list;
    }

    public async Task<IReadOnlyDictionary<string, int>> GetCitationBacklinkCountsAsync(
        IReadOnlyList<string> drawerIds, string? wing, CancellationToken ct)
    {
        if (drawerIds.Count == 0)
            return new Dictionary<string, int>();

        using var ro = _dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        var paramNames = new string[drawerIds.Count];
        for (int i = 0; i < drawerIds.Count; i++)
        {
            paramNames[i] = $"$did{i}";
            cmd.Parameters.Add(new DuckDBParameter($"did{i}", drawerIds[i]));
        }
        var inList = string.Join(",", paramNames);
        var wingFilter = wing is not null ? "AND wing = $wing" : "";
        cmd.CommandText = $"""
            SELECT cited_id, COALESCE(array_length(backlinks), 0) AS bl_count
            FROM wiki_pages, LATERAL UNNEST(citations) AS t(cited_id)
            WHERE cited_id IN ({inList})
              {wingFilter}
            """;
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));

        var result = new Dictionary<string, int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var citedId = reader.GetString(0);
            var blCount = reader.GetInt32(1);
            ref int current = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(result, citedId, out _);
            current = Math.Max(current, blCount);
        }
        return result;
    }

    private async Task ValidateCitationsAsync(
        IReadOnlyList<string> citations, CancellationToken ct)
    {
        if (citations.Count == 0)
            return;

        foreach (var c in citations)
        {
            if (!PathValidator.DrawerIdRegex().IsMatch(c))
                throw new ArgumentException(
                    $"Invalid drawer ID '{c}'. Expected 16-char hex (e.g. 696d52e528f355da) " +
                    $"from AddMemory/SearchMemories response. This is not a file path.");
        }

        using var ro = _dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        var paramNames = new List<string>(citations.Count);
        for (int i = 0; i < citations.Count; i++)
        {
            paramNames.Add($"$c{i}");
            cmd.Parameters.Add(new DuckDBParameter($"c{i}", citations[i]));
        }
        cmd.CommandText = $"SELECT id FROM drawers WHERE id IN ({string.Join(",", paramNames)})";

        var found = new HashSet<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            found.Add(reader.GetString(0));

        var missing = new List<string>();
        foreach (var c in citations)
            if (!found.Contains(c))
                missing.Add(c);

        if (missing.Count > 0)
            throw new ArgumentException(
                $"Citations reference non-existent drawers: {string.Join(", ", missing)}");
    }

    private async Task<bool> PathExistsAsync(string path, CancellationToken ct)
    {
        using var ro = _dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM wiki_pages WHERE path = $path LIMIT 1";
        cmd.Parameters.Add(new DuckDBParameter("path", path));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    private async Task AppendLogAsync(
        string wing, string eventType, string? pagePath, string summary,
        CancellationToken ct)
    {
        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO wiki_log (id, wing, event_type, page_path, summary, occurred_at)
                VALUES ($id, $wing, $type, $path, $summary, now())
                """;
            cmd.Parameters.Add(new DuckDBParameter("id", Guid.NewGuid().ToString("N")));
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
            cmd.Parameters.Add(new DuckDBParameter("type", eventType));
            cmd.Parameters.Add(new DuckDBParameter("path", (object?)pagePath ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("summary", summary));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    [GeneratedRegex(@"\[\[([a-z][a-z0-9-]*(?:/[a-z][a-z0-9-]*)*)\]\]")]
    private static partial Regex WikilinkRegex();

    private async Task RebuildBacklinksAsync(string? wing, CancellationToken ct)
    {
        using var ro = _dbFactory.OpenReadOnly();
        using var sel = ro.CreateCommand();
        sel.CommandText = wing is null
            ? "SELECT path, content FROM wiki_pages"
            : "SELECT path, content FROM wiki_pages WHERE wing = $wing";
        if (wing is not null)
            sel.Parameters.Add(new DuckDBParameter("wing", wing));

        var sourcesByTarget = new Dictionary<string, HashSet<string>>();
        var allPaths = new HashSet<string>();

        using (var reader = await sel.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var p = reader.GetString(0);
                var c = reader.GetString(1);
                allPaths.Add(p);
                foreach (Match m in WikilinkRegex().Matches(c))
                {
                    var target = m.Groups[1].Value;
                    if (!sourcesByTarget.TryGetValue(target, out var set))
                        sourcesByTarget[target] = set = new HashSet<string>();
                    if (target != p)
                        set.Add(p);
                }
            }
        }

        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            foreach (var path in allPaths)
            {
                sourcesByTarget.TryGetValue(path, out var sources);
                var arr = sources is null ? Array.Empty<string>() : sources.ToArray();

                using var upd = db.CreateCommand();
                var paramNames = new List<string>();
                for (int i = 0; i < arr.Length; i++)
                {
                    paramNames.Add($"$b{i}");
                    upd.Parameters.Add(new DuckDBParameter($"b{i}", arr[i]));
                }
                var lit = arr.Length == 0 ? "[]" : $"[{string.Join(",", paramNames)}]";
                upd.CommandText = $"UPDATE wiki_pages SET backlinks = {lit} WHERE path = $path";
                upd.Parameters.Add(new DuckDBParameter("path", path));
                await upd.ExecuteNonQueryAsync(ct);
            }
        }, ct);
    }

    private async Task<List<string>> FindOrphansAsync(string? wing, CancellationToken ct)
    {
        using var ro = _dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = wing is null
            ? """
              SELECT path FROM wiki_pages
              WHERE array_length(backlinks) = 0
                AND content NOT LIKE '%[[%'
              """
            : """
              SELECT path FROM wiki_pages
              WHERE wing = $wing
                AND array_length(backlinks) = 0
                AND content NOT LIKE '%[[%'
              """;
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));

        var list = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    private async Task<List<BrokenCitation>> FindBrokenCitationsAsync(
        string? wing, CancellationToken ct)
    {
        using var ro = _dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = wing is null
            ? """
              SELECT p.path, c
              FROM wiki_pages p, UNNEST(p.citations) AS c
              WHERE c NOT IN (SELECT id FROM drawers)
              """
            : """
              SELECT p.path, c
              FROM wiki_pages p, UNNEST(p.citations) AS c
              WHERE p.wing = $wing
                AND c NOT IN (SELECT id FROM drawers)
              """;
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));

        var list = new List<BrokenCitation>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new BrokenCitation(reader.GetString(0), reader.GetString(1)));
        return list;
    }

    private async Task<List<string>> FindStalePagesAsync(
        string? wing, CancellationToken ct)
    {
        using var ro = _dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = wing is null
            ? "SELECT path FROM wiki_pages WHERE updated_at < now() - INTERVAL '90 days'"
            : "SELECT path FROM wiki_pages WHERE wing = $wing AND updated_at < now() - INTERVAL '90 days'";
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));

        var list = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    private async Task RebuildFtsAsync(CancellationToken ct)
    {
        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                PRAGMA create_fts_index(
                    'wiki_pages', 'path', 'content', 'title',
                    stemmer = 'none', stopwords = 'none', "ignore" = '([^a-zA-Z0-9_])+',
                    overwrite=1
                )
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    private static IReadOnlyList<string> ReadStringArray(System.Data.Common.DbDataReader reader, int ord)
    {
        if (reader.IsDBNull(ord))
            return Array.Empty<string>();
        var raw = reader.GetValue(ord);
        if (raw is List<object> list)
        {
            var result = new List<string>(list.Count);
            foreach (var item in list)
                if (item is string s)
                    result.Add(s);
            return result;
        }
        if (raw is string[] arr)
            return arr;
        if (raw is IEnumerable<string> strs)
            return strs.ToList();
        return Array.Empty<string>();
    }

    static float ComputeWikiQuality(string content, IReadOnlyList<string> citations, string path)
    {
        float score = 0f;
        int wordCount = content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        score += Math.Min(0.30f, wordCount / 150f * 0.30f);
        if (citations.Count > 0)
            score += 0.30f;
        if (WikilinkRegex().IsMatch(content))
            score += 0.20f;
        if (content.Contains("\n##") || content.StartsWith("##"))
            score += 0.10f;
        if (path.Contains('/'))
            score += 0.10f;
        return Math.Clamp(score, 0f, 1f);
    }
}

public enum WriteResult { Created, Updated }
