using System.Security.Cryptography;
using System.Text;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Wendmem.Services;
using Wendmem.Storage;

namespace Wendmem.Wiki;

public sealed record PendingUpdate(
    string PagePath, string DrawerId, float Similarity, DateTimeOffset QueuedAt);

public sealed class PendingUpdateService(
    DuckDbConnectionFactory dbFactory,
    IEmbedder embedder,
    ILogger<PendingUpdateService> logger)
{
    public async Task QueueAsync(
        IReadOnlyList<string> newDrawerIds, string wing,
        float threshold = 0.55f, CancellationToken ct = default)
    {
        if (newDrawerIds.Count == 0)
            return;

        // Load embeddings for the new drawers
        var drawerEmbeddings = await LoadDrawerEmbeddingsAsync(newDrawerIds, ct);
        if (drawerEmbeddings.Count == 0)
            return;

        // Load all wiki page embeddings for this wing
        var pageEmbeddings = await LoadPageEmbeddingsAsync(wing, ct);
        if (pageEmbeddings.Count == 0)
            return;

        var rows = new List<(string Id, string Wing, string PagePath, string DrawerId, float Similarity)>();

        foreach (var (drawerId, dVec) in drawerEmbeddings)
        {
            foreach (var (pagePath, pVec) in pageEmbeddings)
            {
                float sim = CosineSimilarity(dVec, pVec);
                if (sim >= threshold)
                {
                    var id = ComputeId(pagePath, drawerId);
                    rows.Add((id, wing, pagePath, drawerId, sim));
                }
            }
        }

        if (rows.Count == 0)
            return;

        await dbFactory.ExecuteWriteAsync(async db =>
        {
            foreach (var (id, w, pagePath, drawerId, sim) in rows)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO wiki_pending_updates (id, wing, page_path, drawer_id, similarity, queued_at)
                    VALUES ($id, $wing, $page_path, $drawer_id, $similarity, now())
                    ON CONFLICT (id) DO NOTHING
                    """;
                cmd.Parameters.Add(new DuckDBParameter("id", id));
                cmd.Parameters.Add(new DuckDBParameter("wing", w));
                cmd.Parameters.Add(new DuckDBParameter("page_path", pagePath));
                cmd.Parameters.Add(new DuckDBParameter("drawer_id", drawerId));
                cmd.Parameters.Add(new DuckDBParameter("similarity", sim));
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }, ct);

        logger.LogInformation("Queued {Count} pending updates for wing {Wing}", rows.Count, wing);
    }

    public async Task<IReadOnlyList<PendingUpdate>> ListPendingAsync(
        string? wing, string? pagePath, int limit = 50, CancellationToken ct = default)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();

        var conditions = new List<string> { "resolved_at IS NULL" };
        if (wing is not null)
            conditions.Add("wing = $wing");
        if (pagePath is not null)
            conditions.Add("page_path = $page_path");
        var where = string.Join(" AND ", conditions);

        cmd.CommandText = $"""
            SELECT page_path, drawer_id, similarity, queued_at
            FROM wiki_pending_updates
            WHERE {where}
            ORDER BY similarity DESC
            LIMIT $limit
            """;
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        if (pagePath is not null)
            cmd.Parameters.Add(new DuckDBParameter("page_path", pagePath));
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));

        var list = new List<PendingUpdate>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new PendingUpdate(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetFloat(2),
                new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero)));
        }
        return list;
    }

    public async Task ResolveAsync(string pagePath, string drawerId, string resolution,
        CancellationToken ct = default)
    {
        var id = ComputeId(pagePath, drawerId);
        await dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE wiki_pending_updates
                SET resolved_at = now(), resolution = $resolution
                WHERE id = $id
                """;
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            cmd.Parameters.Add(new DuckDBParameter("resolution", resolution));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    /// <summary>
    /// Resolve all pending updates for a page where the drawer_id is in the given citations list.
    /// Called after WikiWrite to auto-resolve matching pending updates.
    /// </summary>
    public async Task ResolveForCitationsAsync(
        string pagePath, IReadOnlyList<string> drawerIds, string resolution,
        CancellationToken ct = default)
    {
        if (drawerIds.Count == 0)
            return;

        await dbFactory.ExecuteWriteAsync(async db =>
        {
            foreach (var drawerId in drawerIds)
            {
                var id = ComputeId(pagePath, drawerId);
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    UPDATE wiki_pending_updates
                    SET resolved_at = now(), resolution = $resolution
                    WHERE id = $id AND resolved_at IS NULL
                    """;
                cmd.Parameters.Add(new DuckDBParameter("id", id));
                cmd.Parameters.Add(new DuckDBParameter("resolution", resolution));
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }, ct);
    }

    public async Task<IReadOnlyDictionary<string, int>> SummaryAsync(
        string wing, CancellationToken ct = default)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT page_path, count(*) AS cnt
            FROM wiki_pending_updates
            WHERE wing = $wing AND resolved_at IS NULL
            GROUP BY page_path
            ORDER BY cnt DESC
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));

        var dict = new Dictionary<string, int>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            dict[reader.GetString(0)] = (int)reader.GetInt64(1);
        return dict;
    }

    static string ComputeId(string pagePath, string drawerId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{pagePath}|{drawerId}"));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    async Task<List<(string DrawerId, float[] Embedding)>> LoadDrawerEmbeddingsAsync(
        IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];

        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        var inList = string.Join(",", ids.Select(id => $"'{id}'"));
        cmd.CommandText = $"""
            SELECT id, embedding FROM drawers
            WHERE id IN ({inList}) AND embedding IS NOT NULL
            """;

        var list = new List<(string, float[])>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var raw = reader.GetValue(1);
            float[] vec = ToFloatArray(raw);
            list.Add((reader.GetString(0), vec));
        }
        return list;
    }

    async Task<List<(string PagePath, float[] Embedding)>> LoadPageEmbeddingsAsync(
        string wing, CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT path, embedding FROM wiki_pages
            WHERE wing = $wing AND embedding IS NOT NULL
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));

        var list = new List<(string, float[])>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var raw = reader.GetValue(1);
            float[] vec = ToFloatArray(raw);
            list.Add((reader.GetString(0), vec));
        }
        return list;
    }

    static float[] ToFloatArray(object raw) => raw switch
    {
        float[] f => f,
        List<float> lf => lf.ToArray(),
        IReadOnlyList<float> rlf => rlf.ToArray(),
        object[] o => Array.ConvertAll(o, x => Convert.ToSingle(x)),
        System.Collections.ICollection c => c.Cast<float>().ToArray(),
        _ => throw new InvalidCastException($"Unexpected embedding type: {raw.GetType()}")
    };

    static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0f, na = 0f, nb = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return dot / (MathF.Sqrt(na) * MathF.Sqrt(nb) + 1e-9f);
    }
}
