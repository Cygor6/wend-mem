using DuckDB.NET.Data;
using Wendmem.Storage;

namespace Wendmem.Experiences;

public sealed class TaskMemoryStorage
{
    readonly DuckDbConnectionFactory _dbFactory;

    public TaskMemoryStorage(DuckDbConnectionFactory dbFactory) { _dbFactory = dbFactory; }

    /// <summary>Insert or skip if id exists. Idempotent. Returns the id.</summary>
    public async Task<string> AddAsync(TaskMemory mem, CancellationToken ct)
    {
        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            await using var cmd = db.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO task_memories
                    (id, wing, when_to_use, content, score, author,
                     keywords, tools_used, source, embedding,
                     time_created, time_modified)
                VALUES ($id, $wing, $when, $content, $score, $author,
                        {ListLiteral(mem.Keywords)}, {ListLiteral(mem.ToolsUsed)},
                        $source, {EmbeddingLiteralOrNull(mem.Embedding)},
                        $created, $modified)
                ON CONFLICT (id) DO NOTHING
                """;
            cmd.Parameters.Add(new DuckDBParameter("id", mem.Id));
            cmd.Parameters.Add(new DuckDBParameter("wing", mem.Wing));
            cmd.Parameters.Add(new DuckDBParameter("when", mem.WhenToUse));
            cmd.Parameters.Add(new DuckDBParameter("content", mem.Content));
            cmd.Parameters.Add(new DuckDBParameter("score", (double)mem.Score));
            cmd.Parameters.Add(new DuckDBParameter("author", mem.Author));
            cmd.Parameters.Add(new DuckDBParameter("source", mem.Source.ToString().ToLowerInvariant()));
            cmd.Parameters.Add(new DuckDBParameter("created", mem.TimeCreated.UtcDateTime));
            cmd.Parameters.Add(new DuckDBParameter("modified", mem.TimeModified.UtcDateTime));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
        return mem.Id;
    }

    public async Task<TaskMemory?> GetAsync(string id, CancellationToken ct)
    {
        await using var ro = _dbFactory.OpenReadOnly();
        await using var cmd = ro.CreateCommand();
        cmd.CommandText = "SELECT * FROM task_memories WHERE id = $id LIMIT 1";
        cmd.Parameters.Add(new DuckDBParameter("id", id));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM task_memories WHERE id = $id";
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public async Task<long> CountAsync(string? wing, CancellationToken ct)
    {
        await using var ro = _dbFactory.OpenReadOnly();
        await using var cmd = ro.CreateCommand();
        cmd.CommandText = wing is null
            ? "SELECT count(*) FROM task_memories"
            : "SELECT count(*) FROM task_memories WHERE wing = $wing";
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    public async Task<IReadOnlyList<TaskMemory>> ListByWingAsync(
        string wing, int limit, CancellationToken ct)
    {
        await using var ro = _dbFactory.OpenReadOnly();
        await using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM task_memories
            WHERE wing = $wing
            ORDER BY time_created DESC
            LIMIT $limit
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<TaskMemory>();
        while (await reader.ReadAsync(ct))
            results.Add(Map(reader));
        return results;
    }

    /// <summary>Find existing rows whose embedding is close to a candidate.</summary>
    public async Task<IReadOnlyList<TaskMemoryResult>> FindNearDuplicatesAsync(
        string wing, float[] candidateEmbedding, float threshold, CancellationToken ct)
    {
        var literal = EmbeddingLiteral(candidateEmbedding);
        await using var ro = _dbFactory.OpenReadOnly();
        await using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT *,
                   array_cosine_similarity(embedding, {literal}) AS sim
            FROM task_memories
            WHERE wing = $wing
              AND embedding IS NOT NULL
              AND array_cosine_similarity(embedding, {literal}) >= $threshold
            ORDER BY sim DESC
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        cmd.Parameters.Add(new DuckDBParameter("threshold", (double)threshold));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<TaskMemoryResult>();
        while (await reader.ReadAsync(ct))
        {
            var sim = reader.GetFloat(reader.GetOrdinal("sim"));
            results.Add(new TaskMemoryResult(Map(reader), sim));
        }
        return results;
    }

    /// <summary>Increment retrieval_count and update last_used_at.</summary>
    public async Task RecordRetrievalAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
            return;

        var placeholders = string.Join(",", idList.Select((_, i) => $"$id{i}"));
        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            await using var cmd = db.CreateCommand();
            cmd.CommandText = $"""
                UPDATE task_memories
                SET retrieval_count = retrieval_count + 1,
                    last_used_at = now()
                WHERE id IN ({placeholders})
                """;
            for (var i = 0; i < idList.Count; i++)
                cmd.Parameters.Add(new DuckDBParameter($"id{i}", idList[i]));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    /// <summary>Increment utility_count for memories that contributed to success.</summary>
    public async Task RecordUtilityAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
            return;

        var placeholders = string.Join(",", idList.Select((_, i) => $"$id{i}"));
        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            await using var cmd = db.CreateCommand();
            cmd.CommandText = $"""
                UPDATE task_memories
                SET utility_count = utility_count + 1
                WHERE id IN ({placeholders})
                """;
            for (var i = 0; i < idList.Count; i++)
                cmd.Parameters.Add(new DuckDBParameter($"id{i}", idList[i]));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    /// <summary>Delete memories where retrieval_count >= alpha AND utility/retrieval < beta.</summary>
    public async Task<int> DeletePrunableAsync(
        string wing, int alpha, float beta, CancellationToken ct)
    {
        return await _dbFactory.ExecuteWriteAsync(async db =>
        {
            await using var cmd = db.CreateCommand();
            cmd.CommandText = """
                DELETE FROM task_memories
                WHERE wing = $wing
                  AND retrieval_count >= $alpha
                  AND CAST(utility_count AS FLOAT) / retrieval_count < $beta
                """;
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
            cmd.Parameters.Add(new DuckDBParameter("alpha", alpha));
            cmd.Parameters.Add(new DuckDBParameter("beta", (double)beta));
            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    internal static string EmbeddingLiteral(float[] vec) =>
        $"[{string.Join(",", vec.Select(f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}]::FLOAT[{vec.Length}]";

    internal static string EmbeddingLiteralOrNull(float[]? vec) =>
        vec is null ? "NULL" : EmbeddingLiteral(vec);

    internal static string ListLiteral(string[] items)
    {
        if (items.Length == 0)
            return "[]::VARCHAR[]";
        var escaped = items.Select(s => $"'{s.Replace("'", "''")}'");
        return $"[{string.Join(",", escaped)}]::VARCHAR[]";
    }

    internal static TaskMemory Map(System.Data.Common.DbDataReader r) => new(
        Id: r.GetString(r.GetOrdinal("id")),
        Wing: r.GetString(r.GetOrdinal("wing")),
        WhenToUse: r.GetString(r.GetOrdinal("when_to_use")),
        Content: r.GetString(r.GetOrdinal("content")),
        Score: r.GetFloat(r.GetOrdinal("score")),
        Author: r.GetString(r.GetOrdinal("author")),
        Keywords: ReadStringArray(r, "keywords"),
        ToolsUsed: ReadStringArray(r, "tools_used"),
        Source: Enum.Parse<TaskMemorySource>(r.GetString(r.GetOrdinal("source")), ignoreCase: true),
        RetrievalCount: r.GetInt32(r.GetOrdinal("retrieval_count")),
        UtilityCount: r.GetInt32(r.GetOrdinal("utility_count")),
        Embedding: null,
        TimeCreated: new DateTimeOffset(r.GetDateTime(r.GetOrdinal("time_created")), TimeSpan.Zero),
        TimeModified: new DateTimeOffset(r.GetDateTime(r.GetOrdinal("time_modified")), TimeSpan.Zero),
        LastUsedAt: r.IsDBNull(r.GetOrdinal("last_used_at"))
                            ? null
                            : new DateTimeOffset(r.GetDateTime(r.GetOrdinal("last_used_at")), TimeSpan.Zero));

    internal static string[] ReadStringArray(System.Data.Common.DbDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        if (r.IsDBNull(ord))
            return [];
        var value = r.GetValue(ord);
        return value switch
        {
            string[] arr => arr,
            IEnumerable<object> items => items.Select(o => o?.ToString() ?? "").ToArray(),
            _ => []
        };
    }
}
