using System.Security.Cryptography;
using System.Text;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace Wendmem.Storage;

public sealed class ReflectionDraftStorage(
    DuckDbConnectionFactory dbFactory,
    ILogger<ReflectionDraftStorage> logger)
{
    public async Task<ReflectionDraft> SaveDraftAsync(
        string wing, string question, string suggestedPath,
        string suggestedTitle, string draftContent, string citations,
        CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow;
        var raw = $"{wing}:{question}:{ts.ToUnixTimeSeconds()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var id = Convert.ToHexString(hash[..8]).ToLowerInvariant();

        await dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO reflection_drafts (id, wing, question, suggested_path,
                    suggested_title, draft_content, citations, created_at, status)
                VALUES ($id, $wing, $question, $path, $title, $content, $citations, now(), 'pending')
                ON CONFLICT (id) DO NOTHING
                """;
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
            cmd.Parameters.Add(new DuckDBParameter("question", question));
            cmd.Parameters.Add(new DuckDBParameter("path", suggestedPath));
            cmd.Parameters.Add(new DuckDBParameter("title", suggestedTitle));
            cmd.Parameters.Add(new DuckDBParameter("content", draftContent));
            cmd.Parameters.Add(new DuckDBParameter("citations", citations));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        logger.LogInformation("Saved reflection draft {Id} wing={Wing}", id, wing);
        return new ReflectionDraft(id, wing, question, suggestedPath, suggestedTitle,
            draftContent, citations, ts, "pending");
    }

    public async Task<List<ReflectionDraft>> ListPendingAsync(string wing, int limit, CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT id, wing, question, suggested_path, suggested_title,
                   draft_content, citations, created_at, status
            FROM reflection_drafts
            WHERE wing = $wing AND status = 'pending'
            ORDER BY created_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));

        var results = new List<ReflectionDraft>();
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (reader.Read())
            results.Add(ReadDraft(reader));
        return results;
    }

    /// <summary>
    /// Distinct questions previously asked for a wing, most recent first.
    /// Used by the reflection dedup guard. Deliberately includes ALL statuses:
    /// a rejected draft should also block re-asking — otherwise the next
    /// reflection regenerates exactly what the user just said no to.
    /// </summary>
    public async Task<IReadOnlyList<string>> RecentQuestionsAsync(
        string wing, int limit, CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT question
            FROM reflection_drafts
            WHERE wing = $wing
            GROUP BY question
            ORDER BY max(created_at) DESC
            LIMIT $limit
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));

        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    public async Task<ReflectionDraft?> GetByIdAsync(string id, CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT id, wing, question, suggested_path, suggested_title,
                   draft_content, citations, created_at, status
            FROM reflection_drafts WHERE id = $id
            """;
        cmd.Parameters.Add(new DuckDBParameter("id", id));

        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        return reader.Read() ? ReadDraft(reader) : null;
    }

    public async Task<bool> UpdateStatusAsync(string id, string status, CancellationToken ct)
    {
        var affected = 0;
        await dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE reflection_drafts SET status = $status WHERE id = $id";
            cmd.Parameters.Add(new DuckDBParameter("status", status));
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            affected = await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
        return affected > 0;
    }

    static ReflectionDraft ReadDraft(DuckDBDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetString(3), reader.GetString(4), reader.GetString(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? DateTimeOffset.UtcNow : new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero),
        reader.GetString(8));
}

public sealed record ReflectionDraft(
    string Id, string Wing, string Question,
    string SuggestedPath, string SuggestedTitle,
    string DraftContent, string Citations,
    DateTimeOffset CreatedAt, string Status);
