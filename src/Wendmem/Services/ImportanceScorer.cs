using System.Text;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Wendmem.Experiences;
using Wendmem.Storage;

namespace Wendmem.Services;

/// <summary>
/// Heuristic + optional LLM salience scorer for drawers.
/// Scores drawers.importance 0.0–1.0 based on decision markers, entity density,
/// numeric density, length normalisation, and KG triple density.
/// </summary>
public sealed partial class ImportanceScorer
{
    readonly DuckDbConnectionFactory? _dbFactory;
    readonly LlmService? _llm;
    readonly ILogger<ImportanceScorer> _logger;

    public ImportanceScorer(
        DuckDbConnectionFactory? dbFactory,
        LlmService? llm,
        ILogger<ImportanceScorer> logger)
    {
        _dbFactory = dbFactory;
        _llm = llm;
        _logger = logger;
    }

    /// <summary>
    /// Compute a heuristic importance score for a single content string.
    /// Deterministic given the same content and entity names.
    /// </summary>
    public float ScoreHeuristic(string content, IReadOnlyList<string>? entityNames = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            return 0.1f;

        float score = 0f;

        // 1. Decision markers (0–0.30)
        score += DecisionMarkerScore(content);

        // 2. Named entity match (0–0.20)
        score += EntityMatchScore(content, entityNames);

        // 3. Numeric density (0–0.15)
        score += NumericDensityScore(content);

        // 4. Length normalisation (0–0.15)
        score += LengthNormalisationScore(content);

        // 5. KG triple density (0–0.20)
        // Caller should pass triple count if available; we estimate from content
        score += TripleDensityScore(content);

        return Math.Clamp(score, 0f, 1f);
    }

    /// <summary>
    /// Batch heuristic scoring: score all drawers in a wing and update importance.
    /// Returns the number of drawers updated.
    /// </summary>
    public async Task<int> RescoreWingAsync(
        string wing, int? limit, CancellationToken ct)
    {
        _logger.LogInformation("Rescoring wing {Wing} limit={Limit}", wing, limit);

        var entityNames = await LoadEntityNamesAsync(ct);
        var drawerRows = await LoadDrawersForScoringAsync(wing, limit, ct);

        if (drawerRows.Count == 0)
        {
            _logger.LogInformation("No drawers to rescore in wing {Wing}", wing);
            return 0;
        }

        var updates = new List<(string Id, float Score)>();
        foreach (var (id, content) in drawerRows)
        {
            var score = ScoreHeuristic(content, entityNames);
            updates.Add((id, score));
        }

        await PersistScoresAsync(updates, ct);

        _logger.LogInformation("Rescored {Count} drawers in wing {Wing}", updates.Count, wing);
        return updates.Count;
    }

    /// <summary>
    /// LLM-mode: batch score drawers using a single LLM call.
    /// Layered on top of heuristic — each drawer gets max(heuristic, llm_score).
    /// </summary>
    public async Task<int> RescoreWingWithLlmAsync(
        string wing, int? limit, CancellationToken ct)
    {
        if (_llm is null)
        {
            _logger.LogDebug("LLM not available for rescoring");
            return 0;
        }

        // First run heuristic
        var entityNames = await LoadEntityNamesAsync(ct);
        var drawerRows = await LoadDrawersForScoringAsync(wing, limit, ct);

        if (drawerRows.Count == 0)
            return 0;

        // Compute heuristic scores first
        var heuristicScores = new Dictionary<string, float>();
        foreach (var (id, content) in drawerRows)
            heuristicScores[id] = ScoreHeuristic(content, entityNames);

        // Batch LLM scoring in groups of 20
        const int batchSize = 20;
        var updates = new List<(string Id, float Score)>();

        for (int i = 0; i < drawerRows.Count; i += batchSize)
        {
            var batch = drawerRows.Skip(i).Take(batchSize).ToList();
            var llmScores = await LlmScoreBatchAsync(batch, ct);

            foreach (var (id, content) in batch)
            {
                var heuristic = heuristicScores[id];
                var llmScore = llmScores.GetValueOrDefault(id, heuristic);
                // Take the max of heuristic and LLM score
                updates.Add((id, Math.Max(heuristic, llmScore)));
            }
        }

        await PersistScoresAsync(updates, ct);

        _logger.LogInformation("LLM rescoring complete: {Count} drawers in wing {Wing}", updates.Count, wing);
        return updates.Count;
    }

    // --- Heuristic components ---

    [GeneratedRegex(
        @"\b(decided|chose|must|will|because|therefore|critical|important|essential|mandatory|required|blocker|urgent|resolved|agreed)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DecisionMarkerRegex();

    static float DecisionMarkerScore(string content)
    {
        var matches = DecisionMarkerRegex().Matches(content);
        // Each marker contributes ~0.06, capped at 0.30 (5 markers)
        return Math.Min(0.30f, matches.Count * 0.06f);
    }

    static float EntityMatchScore(string content, IReadOnlyList<string>? entityNames)
    {
        if (entityNames is null or { Count: 0 })
            return 0f;

        var lower = content.ToLowerInvariant();
        int matches = 0;
        foreach (var name in entityNames)
        {
            if (lower.Contains(name.ToLowerInvariant()))
                matches++;
        }

        // Each matched entity contributes 0.05, capped at 0.20 (4 entities)
        return Math.Min(0.20f, matches * 0.05f);
    }

    [GeneratedRegex(@"\b\d+(?:\.\d+)?\b")]
    private static partial Regex NumericTokenRegex();

    static float NumericDensityScore(string content)
    {
        var tokens = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return 0f;

        int numericTokens = NumericTokenRegex().Matches(content).Count;
        float density = (float)numericTokens / tokens.Length;

        // High numeric density (config/spec content) gets a boost, capped at 0.15
        return Math.Min(0.15f, density * 1.5f);
    }

    static float LengthNormalisationScore(string content)
    {
        var len = content.Length;

        // Sweet spot: 200–2000 chars → 0.15
        // Too short (<50) or too long (>5000) → penalised
        if (len < 50)
            return 0.03f;
        if (len < 200)
            return 0.08f;
        if (len <= 2000)
            return 0.15f;
        if (len <= 5000)
            return 0.10f;
        return 0.05f;
    }

    [GeneratedRegex(@"\b(is|are|was|were|has|have|uses|depends|requires|provides|implements)\s+\S+")]
    private static partial Regex RelationalPatternRegex();

    static float TripleDensityScore(string content)
    {
        // Estimate relational/triple-like patterns in content
        var matches = RelationalPatternRegex().Matches(content);
        // Each relational pattern contributes ~0.07, capped at 0.20 (≈3 patterns)
        return Math.Min(0.20f, matches.Count * 0.07f);
    }

    private async Task<IReadOnlyList<string>> LoadEntityNamesAsync(CancellationToken ct)
    {
        if (_dbFactory is null)
            return [];
        var names = new List<string>();
        await using var ro = _dbFactory.OpenReadOnly();
        await using var cmd = ro.CreateCommand();
        cmd.CommandText = "SELECT name FROM entities";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            names.Add(reader.GetString(0));
        return names;
    }

    private async Task<List<(string Id, string Content)>> LoadDrawersForScoringAsync(
        string wing, int? limit, CancellationToken ct)
    {
        if (_dbFactory is null)
            return [];
        var limitClause = limit.HasValue ? $"LIMIT {limit.Value}" : "";
        await using var ro = _dbFactory.OpenReadOnly();
        await using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, content
            FROM drawers
            WHERE wing = $wing
              AND is_representative = TRUE
              AND valid_to IS NULL
            ORDER BY mined_at DESC
            {limitClause}
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));

        var rows = new List<(string Id, string Content)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    private async Task PersistScoresAsync(
        IReadOnlyList<(string Id, float Score)> updates, CancellationToken ct)
    {
        if (_dbFactory is null)
            return;
        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            foreach (var (id, score) in updates)
            {
                await using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    UPDATE drawers SET importance = $score WHERE id = $id
                    """;
                cmd.Parameters.Add(new DuckDBParameter("score", (double)score));
                cmd.Parameters.Add(new DuckDBParameter("id", id));
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }, ct);
    }

    private async Task<Dictionary<string, float>> LlmScoreBatchAsync(
        IReadOnlyList<(string Id, string Content)> batch, CancellationToken ct)
    {
        if (_llm is null)
            return [];

        var sb = new StringBuilder();
        sb.AppendLine("Score each observation for importance to a developer's long-term memory.");
        sb.AppendLine("0.0 = trivial noise, 0.5 = moderately useful, 1.0 = critical decision or insight.");
        sb.AppendLine("Respond ONLY with a JSON object mapping ID to float score.");
        sb.AppendLine("Example: {\"a3f2b1c8d4e5f607\": 0.8, ...}");
        sb.AppendLine();

        for (int i = 0; i < batch.Count; i++)
        {
            var snippet = batch[i].Content.Length > 200
                ? batch[i].Content[..200] + "..."
                : batch[i].Content;
            sb.AppendLine($"{i + 1}. [{batch[i].Id}] {snippet}");
        }

        try
        {
            var raw = await _llm.CompleteAsync(sb.ToString(), ct);
            return ParseLlmScores(raw);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LLM scoring failed for batch");
            return [];
        }
    }

    private static Dictionary<string, float> ParseLlmScores(string raw)
    {
        var result = new Dictionary<string, float>();
        try
        {
            // Extract JSON object from potentially wrapped response
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start)
                return result;

            var json = raw[start..(end + 1)];
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number &&
                    prop.Value.TryGetSingle(out var score))
                    result[prop.Name] = Math.Clamp(score, 0f, 1f);
            }
        }
        catch
        {
            // Swallow parse errors — caller falls back to heuristic
        }
        return result;
    }
}
