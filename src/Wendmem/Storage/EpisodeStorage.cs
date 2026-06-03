using System.Security.Cryptography;
using System.Text;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Wendmem.Services;

namespace Wendmem.Storage;

public sealed class EpisodeStorage
{
    readonly DuckDbConnectionFactory _dbFactory;
    readonly IEmbedder _embedder;
    readonly ILogger<EpisodeStorage> _logger;

    public EpisodeStorage(
        DuckDbConnectionFactory dbFactory,
        IEmbedder embedder,
        ILogger<EpisodeStorage> logger)
    {
        _dbFactory = dbFactory;
        _embedder = embedder;
        _logger = logger;
    }

    public async Task<Episode> RecordAsync(
        string wing, string goal, string? plan, string outcome,
        string? whatWorked, string? whatFailed, string? nextTime,
        string? drawerRefs, string? skillRefs, string? agent,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var id = ComputeId(wing, goal, startedAt);

        var embedText = string.Join(" ", new[] { goal, nextTime }.Where(s => !string.IsNullOrWhiteSpace(s)));
        float[]? embedding = null;
        if (!string.IsNullOrWhiteSpace(embedText) && _embedder.IsAvailable)
        {
            try
            { embedding = await _embedder.EmbedAsync(embedText, ct); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to embed episode {Id} — episode will be invisible to semantic search until re-embedded", id); }
        }

        var embLit = embedding is not null
            ? EmbeddingUtils.ToFloatArrayLiteral(embedding)
            : null;

        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = embLit is not null
                ? """
                  INSERT INTO episodes (id, wing, goal, plan, outcome, what_worked, what_failed,
                      next_time, drawer_refs, skill_refs, embedding, started_at, ended_at, agent)
                  VALUES ($id, $wing, $goal, $plan, $outcome, $what_worked, $what_failed,
                      $next_time, $drawer_refs, $skill_refs, #embed::FLOAT[512], $started_at, now(), $agent)
                  ON CONFLICT (id) DO NOTHING
                  """
                : """
                  INSERT INTO episodes (id, wing, goal, plan, outcome, what_worked, what_failed,
                      next_time, drawer_refs, skill_refs, embedding, started_at, ended_at, agent)
                  VALUES ($id, $wing, $goal, $plan, $outcome, $what_worked, $what_failed,
                      $next_time, $drawer_refs, $skill_refs, NULL, $started_at, now(), $agent)
                  ON CONFLICT (id) DO NOTHING
                  """;
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
            cmd.Parameters.Add(new DuckDBParameter("goal", goal));
            cmd.Parameters.Add(new DuckDBParameter("plan", (object?)plan ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("outcome", outcome));
            cmd.Parameters.Add(new DuckDBParameter("what_worked", (object?)whatWorked ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("what_failed", (object?)whatFailed ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("next_time", (object?)nextTime ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("drawer_refs", (object?)drawerRefs ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("skill_refs", (object?)skillRefs ?? DBNull.Value));
            if (embLit is not null)
                cmd.CommandText = cmd.CommandText.Replace("#embed", embLit);
            cmd.Parameters.Add(new DuckDBParameter("started_at", startedAt.DateTime));
            cmd.Parameters.Add(new DuckDBParameter("agent", (object?)agent ?? DBNull.Value));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        if (!string.IsNullOrWhiteSpace(skillRefs))
            await UpdateSkillCountsAsync(skillRefs, outcome, ct);

        _logger.LogInformation("Recorded episode {Id} wing={Wing} outcome={Outcome}", id, wing, outcome);

        return new Episode(id, wing, goal, plan, outcome, whatWorked, whatFailed,
            nextTime, drawerRefs, skillRefs, startedAt, DateTimeOffset.UtcNow, agent);
    }

    public async Task<List<EpisodeResult>> FindAsync(
        string query, string? wing, string outcomeFilter, int k,
        CancellationToken ct)
    {
        if (!_embedder.IsAvailable)
            return [];

        var queryEmb = await _embedder.EmbedAsync(query, ct);
        var embLit = EmbeddingUtils.ToFloatArrayLiteral(queryEmb);

        var wingFilter = wing is not null ? "AND wing = $wing" : "";
        var outcomeFilterSql = outcomeFilter is not null && outcomeFilter != "any"
            ? "AND outcome = $outcome" : "";
        const float threshold = 0.55f;

        using var ro = _dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, wing, goal, plan, outcome, what_worked, what_failed,
                   next_time, drawer_refs, skill_refs, started_at, ended_at, agent,
                   array_cosine_similarity(embedding, #qemb::FLOAT[512]) AS score
            FROM episodes
            WHERE embedding IS NOT NULL
              {wingFilter}
              {outcomeFilterSql}
            ORDER BY score DESC
            LIMIT $k
            """.Replace("#qemb", embLit);
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        if (outcomeFilterSql.Length > 0)
            cmd.Parameters.Add(new DuckDBParameter("outcome", outcomeFilter));
        cmd.Parameters.Add(new DuckDBParameter("k", k));

        var results = new List<EpisodeResult>();
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (reader.Read())
        {
            var score = reader.GetFloat(13);
            if (score < threshold)
                continue;
            if (reader.GetString(4) == "failure")
                score += 0.05f;
            results.Add(new EpisodeResult(ReadEpisode(reader), score));
        }
        return results;
    }

    public async Task<List<EpisodeResult>> FindForWakeUpAsync(
        string query, string? wing, int k, CancellationToken ct)
        => await FindAsync(query, wing, "any", k, ct);

    public async Task<List<Episode>> ListAsync(
        string wing, string? outcomeFilter, int limit, CancellationToken ct)
    {
        var outcomeSql = outcomeFilter is not null && outcomeFilter != "any"
            ? "AND outcome = $outcome" : "";

        using var ro = _dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, wing, goal, plan, outcome, what_worked, what_failed,
                   next_time, drawer_refs, skill_refs, started_at, ended_at, agent
            FROM episodes
            WHERE wing = $wing
              {outcomeSql}
            ORDER BY ended_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        if (outcomeSql.Length > 0)
            cmd.Parameters.Add(new DuckDBParameter("outcome", outcomeFilter));
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));

        var episodes = new List<Episode>();
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (reader.Read())
            episodes.Add(ReadEpisode(reader));
        return episodes;
    }

    public async Task<Episode?> GetByIdAsync(string id, CancellationToken ct)
    {
        using var ro = _dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT id, wing, goal, plan, outcome, what_worked, what_failed,
                   next_time, drawer_refs, skill_refs, started_at, ended_at, agent
            FROM episodes WHERE id = $id
            """;
        cmd.Parameters.Add(new DuckDBParameter("id", id));
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        if (!reader.Read())
            return null;
        return ReadEpisode(reader);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        int affected = 0;
        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM episodes WHERE id = $id";
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            affected = await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
        return affected > 0;
    }

    static Episode ReadEpisode(DuckDBDataReader reader)
    {
        return new Episode(
            Id: reader.GetString(0),
            Wing: reader.GetString(1),
            Goal: reader.GetString(2),
            Plan: reader.IsDBNull(3) ? null : reader.GetString(3),
            Outcome: reader.GetString(4),
            WhatWorked: reader.IsDBNull(5) ? null : reader.GetString(5),
            WhatFailed: reader.IsDBNull(6) ? null : reader.GetString(6),
            NextTime: reader.IsDBNull(7) ? null : reader.GetString(7),
            DrawerRefs: reader.IsDBNull(8) ? null : reader.GetString(8),
            SkillRefs: reader.IsDBNull(9) ? null : reader.GetString(9),
            StartedAt: reader.IsDBNull(10) ? DateTimeOffset.UtcNow : new DateTimeOffset(reader.GetDateTime(10), TimeSpan.Zero),
            EndedAt: reader.IsDBNull(11) ? DateTimeOffset.UtcNow : new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero),
            Agent: reader.IsDBNull(12) ? null : reader.GetString(12));
    }

    async Task UpdateSkillCountsAsync(string skillRefs, string outcome, CancellationToken ct)
    {
        var skillIds = skillRefs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (skillIds.Length == 0)
            return;

        var isSuccess = outcome is "success" or "partial";
        var isFailure = outcome is "failure" or "partial";

        await _dbFactory.ExecuteWriteAsync(async db =>
        {
            foreach (var sid in skillIds)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = isSuccess
                    ? "UPDATE skills SET success_count = success_count + 1, last_used_at = now() WHERE id = $id"
                    : isFailure
                        ? "UPDATE skills SET failure_count = failure_count + 1, last_used_at = now() WHERE id = $id"
                        : "UPDATE skills SET last_used_at = now() WHERE id = $id";
                cmd.Parameters.Add(new DuckDBParameter("id", sid));
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }, ct);
    }

    static string ComputeId(string wing, string goal, DateTimeOffset startedAt)
    {
        var raw = $"{wing}:{goal}:{startedAt.ToUnixTimeSeconds()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }
}

public sealed record Episode(
    string Id, string Wing, string Goal, string? Plan, string Outcome,
    string? WhatWorked, string? WhatFailed, string? NextTime,
    string? DrawerRefs, string? SkillRefs,
    DateTimeOffset StartedAt, DateTimeOffset EndedAt, string? Agent);

public sealed record EpisodeResult(Episode Episode, float Score);
