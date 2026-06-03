using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Wendmem.Storage;

namespace Wendmem.Services;

public sealed record ActivityEntry(
    DateTimeOffset Ts, string? Wing, string Action,
    string? Target, string? Agent, string? Summary);

public sealed class ActivityLog(DuckDbConnectionFactory dbFactory, ILogger<ActivityLog> logger)
{
    public async Task LogAsync(
        string action, string? wing, string? target,
        string? agent, string? summary, CancellationToken ct = default)
    {
        try
        {
            var id = Guid.NewGuid().ToString("N");
            await dbFactory.ExecuteWriteAsync(async db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO palace_activity (id, ts, wing, action, target, agent, summary)
                    VALUES ($id, now(), $wing, $action, $target, $agent, $summary)
                    """;
                cmd.Parameters.Add(new DuckDBParameter("id", id));
                cmd.Parameters.Add(new DuckDBParameter("wing", (object?)wing ?? DBNull.Value));
                cmd.Parameters.Add(new DuckDBParameter("action", action));
                cmd.Parameters.Add(new DuckDBParameter("target", (object?)target ?? DBNull.Value));
                cmd.Parameters.Add(new DuckDBParameter("agent", (object?)agent ?? DBNull.Value));
                cmd.Parameters.Add(new DuckDBParameter("summary", (object?)summary ?? DBNull.Value));
                await cmd.ExecuteNonQueryAsync(ct);
            }, ct);
        }
        catch (Exception ex)
        {
            // Fire-and-forget: never throw. Log to stderr only.
            logger.LogError(ex, "Failed to log activity {Action} {Target}", action, target);
        }
    }

    public async Task<IReadOnlyList<ActivityEntry>> RecentAsync(
        string? wing, int limit = 20, CancellationToken ct = default)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        var wingFilter = wing is not null ? "WHERE wing = $wing" : "";
        cmd.CommandText = $"""
            SELECT ts, wing, action, target, agent, summary
            FROM palace_activity
            {wingFilter}
            ORDER BY ts DESC
            LIMIT $limit
            """;
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));

        var list = new List<ActivityEntry>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ActivityEntry(
                new DateTimeOffset(reader.GetDateTime(0), TimeSpan.Zero),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return list;
    }
}
