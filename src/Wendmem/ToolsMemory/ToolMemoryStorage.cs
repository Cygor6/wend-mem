using DuckDB.NET.Data;
using Wendmem.Storage;

namespace Wendmem.ToolsMemory;

public sealed class ToolMemoryStorage
{
    readonly DuckDbConnectionFactory _dbFactory;
    public ToolMemoryStorage(DuckDbConnectionFactory dbFactory) { _dbFactory = dbFactory; }

    public async Task RecordCallAsync(ToolCallResult call, CancellationToken ct)
    {
        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tool_call_history
                    (id, wing, tool_name, input_json, output_json, success, score, summary, token_cost, time_seconds, is_summarized, called_at)
                VALUES ($id, $wing, $tool, $in, $out, $success, $score, $summary, $tok, $time, $summarized, $at)
                ON CONFLICT (id) DO NOTHING
                """;
            cmd.Parameters.Add(new DuckDBParameter("id", call.Id));
            cmd.Parameters.Add(new DuckDBParameter("wing", call.Wing));
            cmd.Parameters.Add(new DuckDBParameter("tool", call.ToolName));
            cmd.Parameters.Add(new DuckDBParameter("in", call.InputJson));
            cmd.Parameters.Add(new DuckDBParameter("out", call.OutputJson));
            cmd.Parameters.Add(new DuckDBParameter("success", call.Success));
            cmd.Parameters.Add(new DuckDBParameter("score", (double)call.Score));
            cmd.Parameters.Add(new DuckDBParameter("summary", (object?)call.Summary ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("tok", call.TokenCost));
            cmd.Parameters.Add(new DuckDBParameter("time", call.TimeSeconds));
            cmd.Parameters.Add(new DuckDBParameter("summarized", call.IsSummarized));
            cmd.Parameters.Add(new DuckDBParameter("at", call.CalledAt.UtcDateTime));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public async Task<IReadOnlyList<ToolCallResult>> GetRecentCallsAsync(string wing, string toolName, int limit, CancellationToken ct)
    {
        await using var ro = _dbFactory.OpenReadOnly();
        await using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT id, wing, tool_name, input_json, output_json, success, score, summary, token_cost, time_seconds, is_summarized, called_at
            FROM tool_call_history WHERE wing = $wing AND tool_name = $tool ORDER BY called_at DESC LIMIT $limit
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        cmd.Parameters.Add(new DuckDBParameter("tool", toolName));
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));
        var results = new List<ToolCallResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapCall(reader));
        return results;
    }

    public async Task<IReadOnlyList<ToolCallResult>> GetUnsummarizedCallsAsync(string wing, string toolName, CancellationToken ct)
    {
        await using var ro = _dbFactory.OpenReadOnly();
        await using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT id, wing, tool_name, input_json, output_json, success, score, summary, token_cost, time_seconds, is_summarized, called_at
            FROM tool_call_history WHERE wing = $wing AND tool_name = $tool AND is_summarized = false ORDER BY called_at DESC
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        cmd.Parameters.Add(new DuckDBParameter("tool", toolName));
        var results = new List<ToolCallResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapCall(reader));
        return results;
    }

    public async Task UpdateCallEvaluationAsync(string id, float score, string summary, CancellationToken ct)
    {
        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE tool_call_history SET score = $score, summary = $summary WHERE id = $id";
            cmd.Parameters.Add(new DuckDBParameter("score", (double)score));
            cmd.Parameters.Add(new DuckDBParameter("summary", summary));
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public async Task MarkCallsSummarizedAsync(string wing, string toolName, IEnumerable<string> ids, CancellationToken ct)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
            return;
        var placeholders = string.Join(",", idList.Select((_, i) => $"$id{i}"));
        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"UPDATE tool_call_history SET is_summarized = true WHERE wing = $wing AND tool_name = $tool AND id IN ({placeholders})";
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
            cmd.Parameters.Add(new DuckDBParameter("tool", toolName));
            for (var i = 0; i < idList.Count; i++)
                cmd.Parameters.Add(new DuckDBParameter($"id{i}", idList[i]));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public async Task<ToolMemoryStatistics> ComputeStatisticsAsync(string wing, string toolName, int recentLimit, CancellationToken ct)
    {
        await using var ro = _dbFactory.OpenReadOnly();
        await using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT count(*) AS total, count(*) FILTER (WHERE success) AS successes,
                   COALESCE(avg(CASE WHEN time_seconds IS NULL THEN 0 ELSE time_seconds END),0) AS avg_time,
                   COALESCE(avg(CASE WHEN token_cost IS NULL THEN 0 ELSE token_cost END),0) AS avg_tokens
            FROM (SELECT * FROM tool_call_history WHERE wing = $wing AND tool_name = $tool ORDER BY called_at DESC LIMIT $limit)
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        cmd.Parameters.Add(new DuckDBParameter("tool", toolName));
        cmd.Parameters.Add(new DuckDBParameter("limit", recentLimit));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new(toolName, 0, 0, 0, 0, 0);
        var total = reader.GetInt32(0);
        var succ = reader.GetInt32(1);
        var avgTime = reader.GetDouble(2);
        var avgTok = reader.GetDouble(3);
        return new(toolName, total, succ, (float)(succ + 1) / (total + 2), avgTime, (int)avgTok);
    }

    public async Task UpsertGuidelinesAsync(ToolMemory mem, CancellationToken ct)
    {
        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tool_memories (wing, tool_name, guidelines, score, author, time_created, time_modified)
                VALUES ($wing, $tool, $g, $s, $a, $created, $modified)
                ON CONFLICT (wing, tool_name) DO UPDATE SET guidelines=EXCLUDED.guidelines, score=EXCLUDED.score, author=EXCLUDED.author, time_modified=EXCLUDED.time_modified
                """;
            cmd.Parameters.Add(new DuckDBParameter("wing", mem.Wing));
            cmd.Parameters.Add(new DuckDBParameter("tool", mem.ToolName));
            cmd.Parameters.Add(new DuckDBParameter("g", mem.Guidelines));
            cmd.Parameters.Add(new DuckDBParameter("s", (double)mem.Score));
            cmd.Parameters.Add(new DuckDBParameter("a", mem.Author));
            cmd.Parameters.Add(new DuckDBParameter("created", mem.TimeCreated.UtcDateTime));
            cmd.Parameters.Add(new DuckDBParameter("modified", mem.TimeModified.UtcDateTime));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public async Task<ToolMemory?> GetGuidelinesAsync(string wing, string toolName, CancellationToken ct)
    {
        await using var ro = _dbFactory.OpenReadOnly();
        await using var cmd = ro.CreateCommand();
        cmd.CommandText = "SELECT wing, tool_name, guidelines, score, author, time_created, time_modified FROM tool_memories WHERE wing = $wing AND tool_name = $tool LIMIT 1";
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        cmd.Parameters.Add(new DuckDBParameter("tool", toolName));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetFloat(3), reader.GetString(4),
            new(reader.GetDateTime(5), TimeSpan.Zero), new(reader.GetDateTime(6), TimeSpan.Zero));
    }

    static ToolCallResult MapCall(System.Data.Common.DbDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4),
        r.GetBoolean(5), r.GetFloat(6), r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? 0 : r.GetInt32(8), r.IsDBNull(9) ? 0.0 : r.GetDouble(9),
        r.GetBoolean(10), new(r.GetDateTime(11), TimeSpan.Zero));
}
