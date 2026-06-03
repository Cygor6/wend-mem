using System.Security.Cryptography;
using System.Text;
using DuckDB.NET.Data;

namespace Wendmem.Storage;

sealed class ClosetStorage(DuckDbConnectionFactory dbFactory)
{
    public async Task<string> AddClosetAsync(
        string drawerId, string aaakText,
        string? wing, string? room, string? sourceFile,
        CancellationToken ct)
    {
        var id = ClosetId(drawerId);

        await dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO closets (id, drawer_id, wing, room, source_file, aaak_text)
                VALUES ($id, $drawer_id, $wing, $room, $source_file, $aaak_text)
                ON CONFLICT (id) DO UPDATE SET
                    aaak_text  = excluded.aaak_text,
                    created_at = now()
                """;
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            cmd.Parameters.Add(new DuckDBParameter("drawer_id", drawerId));
            cmd.Parameters.Add(new DuckDBParameter("wing", wing ?? (object)DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("room", room ?? (object)DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("source_file", sourceFile ?? (object)DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("aaak_text", aaakText));
            await Task.Run(() => cmd.ExecuteNonQuery(), ct);
        }, ct);
        return id;
    }

    public async Task<IReadOnlyList<(string DrawerId, float Score)>> FtsSearchAsync(
        string query, string? wing, int limit, CancellationToken ct)
    {
        try
        {
            using var ro = dbFactory.OpenReadOnly();
            using var cmd = ro.CreateCommand();
            var wingFilter = wing is not null ? "WHERE wing = $wing" : "";
            cmd.CommandText = $"""
                WITH scored AS (
                    SELECT id,
                           fts_main_closets.match_bm25(id, $query, fields := 'aaak_text') AS score
                    FROM closets
                    {wingFilter}
                )
                SELECT c.drawer_id, s.score
                FROM scored s
                JOIN closets c ON c.id = s.id
                WHERE s.score IS NOT NULL
                ORDER BY s.score DESC
                LIMIT $limit
                """;
            cmd.Parameters.Add(new DuckDBParameter("query", query));
            cmd.Parameters.Add(new DuckDBParameter("limit", limit));
            if (wing is not null)
                cmd.Parameters.Add(new DuckDBParameter("wing", wing));

            var results = new List<(string, float)>();
            using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
            while (reader.Read())
                results.Add((reader.GetString(0), Convert.ToSingle(reader.GetValue(1))));
            return results;
        }
        catch (DuckDBException)
        {
            // FTS index doesn't exist yet
            return [];
        }
    }

    public async Task RebuildFtsIndexAsync(CancellationToken ct)
    {
        await dbFactory.ExecuteWriteAsync(db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                PRAGMA create_fts_index('closets', 'id', 'aaak_text',
                    stemmer = 'none', stopwords = 'none',
                    "ignore" = '([^a-zA-Z0-9_])+',
                    lower = 'true', strip_accents = 'true',
                    overwrite=1)
                """;
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }, ct);
    }

    public async Task<string?> GetAaakTextAsync(string drawerId, CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = "SELECT aaak_text FROM closets WHERE drawer_id = $id LIMIT 1";
        cmd.Parameters.Add(new DuckDBParameter("id", drawerId));
        var result = await Task.Run(() => cmd.ExecuteScalar(), ct);
        return result is DBNull or null ? null : (string)result;
    }

    static string ClosetId(string drawerId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(drawerId));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }
}
