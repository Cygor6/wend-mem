using System.Globalization;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Wendmem.Storage;

namespace Wendmem.Services;

/// <summary>
/// Structured side-index that extracts typed tokens (numbers, identifiers,
/// arities, qualified names) from drawer content and stores them for exact-match
/// overlap scoring. This breaks the cosine confusion where "takes 3 args" ≈ "takes 5 args".
/// </summary>
public sealed partial class EntityIndexService
{
    readonly DuckDbConnectionFactory _dbFactory;

    public EntityIndexService(DuckDbConnectionFactory dbFactory) { _dbFactory = dbFactory; }

    [GeneratedRegex(@"\b(\d+(?:\.\d+)?)\b")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\b([A-Z][a-zA-Z0-9]*(?:\.[A-Z][a-zA-Z0-9]*)+)\b")]
    private static partial Regex QualifiedNameRegex();

    [GeneratedRegex(@"\b(0x[0-9a-fA-F]+)\b")]
    private static partial Regex HexLiteralRegex();

    [GeneratedRegex(@"\b([a-z_][a-z0-9_]{2,})\s*\(")]
    private static partial Regex FunctionCallRegex();

    [GeneratedRegex(@"\b(\d+)\s*(?:args?|params?|parameters?|items?)\b")]
    private static partial Regex ArityRegex();

    /// <summary>
    /// Extract structured tokens from text and persist them into drawer_tokens.
    /// Called after a drawer is inserted.
    /// </summary>
    public async Task IndexDrawerAsync(string drawerId, string content, CancellationToken ct)
    {
        var tokens = ExtractTokens(content);
        if (tokens.Count == 0)
            return;

        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            foreach (var (tokenType, tokenValue) in tokens)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO drawer_tokens (drawer_id, token_type, token_value)
                    VALUES ($did, $type, $value)
                    ON CONFLICT (drawer_id, token_type, token_value) DO NOTHING
                    """;
                cmd.Parameters.Add(new DuckDBParameter("did", drawerId));
                cmd.Parameters.Add(new DuckDBParameter("type", tokenType));
                cmd.Parameters.Add(new DuckDBParameter("value", tokenValue));
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }, ct);
    }

    /// <summary>
    /// Delete all tokens for a drawer (used when drawer is deleted).
    /// </summary>
    public async Task DeleteTokensForDrawerAsync(string drawerId, CancellationToken ct)
    {
        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM drawer_tokens WHERE drawer_id = $did";
            cmd.Parameters.Add(new DuckDBParameter("did", drawerId));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    /// <summary>
    /// Compute a structured-overlap boost for a set of drawer IDs against a query.
    /// Returns a dictionary of drawer_id → overlap score (0.0 to 1.0).
    /// The score is the Jaccard overlap of query tokens with drawer tokens.
    /// </summary>
    public async Task<Dictionary<string, float>> OverlapBoostAsync(
        IReadOnlyList<string> drawerIds, string query, CancellationToken ct)
    {
        if (drawerIds.Count == 0)
            return [];

        var queryTokens = ExtractTokens(query);
        if (queryTokens.Count == 0)
            return [];

        var querySet = new HashSet<(string, string)>(queryTokens);

        var idParams = string.Join(",", drawerIds.Select((_, i) => $"$did{i}"));
        await using var ro = _dbFactory.OpenReadOnly();
        await using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT drawer_id, token_type, token_value
            FROM drawer_tokens
            WHERE drawer_id IN ({idParams})
            """;
        for (int i = 0; i < drawerIds.Count; i++)
            cmd.Parameters.Add(new DuckDBParameter($"did{i}", drawerIds[i]));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var drawerTokens = new Dictionary<string, HashSet<(string, string)>>();
        while (await reader.ReadAsync(ct))
        {
            var did = reader.GetString(0);
            var tt = reader.GetString(1);
            var tv = reader.GetString(2);
            if (!drawerTokens.TryGetValue(did, out var set))
                drawerTokens[did] = set = new();
            set.Add((tt, tv));
        }

        var result = new Dictionary<string, float>();
        foreach (var did in drawerIds)
        {
            if (!drawerTokens.TryGetValue(did, out var drawerSet) || drawerSet.Count == 0)
                continue;
            int intersection = querySet.Intersect(drawerSet).Count();
            int union = querySet.Union(drawerSet).Count();
            if (union > 0 && intersection > 0)
                result[did] = (float)intersection / union;
        }
        return result;
    }

    /// <summary>
    /// Find drawer IDs that share any typed token with the query.
    /// Returns drawer_id → count of matching tokens.
    /// </summary>
    public async Task<Dictionary<string, int>> FindByTokenOverlapAsync(
        string query, string? wing, int limit, CancellationToken ct)
    {
        var queryTokens = ExtractTokens(query);
        if (queryTokens.Count == 0)
            return [];

        // Build token-value-only set for looser matching
        var values = queryTokens.Select(t => t.Value).Distinct().ToList();
        var valParams = string.Join(",", values.Select((_, i) => $"$v{i}"));

        var wingJoin = wing is not null
            ? "JOIN drawers d ON d.id = dt.drawer_id AND d.wing = $wing"
            : "";

        await using var ro = _dbFactory.OpenReadOnly();
        await using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT dt.drawer_id, count(*) AS hits
            FROM drawer_tokens dt
            {wingJoin}
            WHERE dt.token_value IN ({valParams})
            GROUP BY dt.drawer_id
            ORDER BY hits DESC
            LIMIT $limit
            """;
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        for (int i = 0; i < values.Count; i++)
            cmd.Parameters.Add(new DuckDBParameter($"v{i}", values[i]));
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new Dictionary<string, int>();
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    static List<(string Type, string Value)> ExtractTokens(string text)
    {
        var tokens = new List<(string, string)>();
        var seen = new HashSet<(string, string)>();

        foreach (Match m in QualifiedNameRegex().Matches(text))
            Add("qualified", m.Groups[1].Value);

        foreach (Match m in HexLiteralRegex().Matches(text))
            Add("hex", m.Groups[1].Value.ToLowerInvariant());

        foreach (Match m in NumberRegex().Matches(text))
        {
            var val = m.Groups[1].Value;
            if (decimal.TryParse(val, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                Add("number", d.ToString("G", CultureInfo.InvariantCulture));
        }

        foreach (Match m in ArityRegex().Matches(text))
            Add("arity", m.Groups[1].Value);

        foreach (Match m in FunctionCallRegex().Matches(text))
            Add("funcall", m.Groups[1].Value);

        return tokens;

        void Add(string type, string value)
        {
            if (seen.Add((type, value)))
                tokens.Add((type, value));
        }
    }
}
