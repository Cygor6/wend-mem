using DuckDB.NET.Data;
using Wendmem.Services;
using Wendmem.Storage;

namespace Wendmem.Experiences;

public record RetrievalResult(
    IReadOnlyList<TaskMemoryResult> Memories,
    string FormattedAnswer)
{
    public static RetrievalResult Empty { get; } = new([], "");
}

public sealed class ExperienceRetriever
{
    readonly DuckDbConnectionFactory _dbFactory;
    readonly IEmbedder _embedder;
    readonly TaskMemoryStorage _storage;
    readonly LlmService? _llm;
    readonly Options.ExperienceOptions _config;

    public ExperienceRetriever(
        DuckDbConnectionFactory dbFactory,
        IEmbedder embedder,
        TaskMemoryStorage storage,
        Options.ExperienceOptions config,
        LlmService? llm = null)
    {
        _dbFactory = dbFactory;
        _embedder = embedder;
        _storage = storage;
        _config = config;
        _llm = llm;
    }

    public async Task<RetrievalResult> SearchAsync(
        string query, string wing, int? k, CancellationToken ct)
    {
        var normalised = query.Trim();
        if (string.IsNullOrEmpty(normalised))
            return RetrievalResult.Empty;

        var topK = k ?? _config.TopK;
        var candidates = await RecallAsync(normalised, wing, topK * 3, ct);

        var reranked = _config.EnableLlmRerank && _llm is not null
            ? await LlmRerankAsync(normalised, candidates, topK, ct)
            : await ScoreBasedRerank(candidates, topK);

        var formatted = _config.EnableLlmRewrite && _llm is not null
            ? await LlmRewriteAsync(normalised, reranked, ct)
            : DirectFormat(reranked);

        await _storage.RecordRetrievalAsync(reranked.Select(r => r.Memory.Id), ct);

        return new RetrievalResult(reranked, formatted);
    }

    public async Task<IReadOnlyList<TaskMemoryResult>> SearchAndRerankAsync(
        string query, string wing, int? k, CancellationToken ct)
    {
        var initial = await SearchAsync(query, wing, k is null ? (int?)null : k * 3, ct);
        var now = DateTimeOffset.UtcNow;
        var reranked = initial.Memories
            .Select(r =>
            {
                var ageDays = (now - r.Memory.TimeCreated).TotalDays;
                var recency = (float)Math.Exp(-ageDays / 30.0);
                var combined = 0.6f * r.SimilarityScore + 0.3f * r.Memory.Score + 0.1f * recency;
                return r with { SimilarityScore = combined };
            })
            .OrderByDescending(r => r.SimilarityScore)
            .Take(k ?? _config.TopK)
            .ToList();
        return reranked;
    }

    async Task<IReadOnlyList<TaskMemoryResult>> RecallAsync(
        string query, string wing, int limit, CancellationToken ct)
    {
        var queryVec = await _embedder.EmbedQueryAsync(query, ct);
        var literal = TaskMemoryStorage.EmbeddingLiteral(queryVec);

        await using var ro = _dbFactory.OpenReadOnly();
        await using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, wing, when_to_use, content, score, author,
                   keywords, tools_used, source,
                   retrieval_count, utility_count,
                   time_created, time_modified, last_used_at,
                   array_cosine_similarity(embedding, {literal}) AS sim
            FROM task_memories
            WHERE wing = $wing AND embedding IS NOT NULL
            ORDER BY sim DESC
            LIMIT $k
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        cmd.Parameters.Add(new DuckDBParameter("k", limit));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<TaskMemoryResult>();
        while (await reader.ReadAsync(ct))
        {
            var sim = reader.GetFloat(reader.GetOrdinal("sim"));
            results.Add(new TaskMemoryResult(TaskMemoryStorage.Map(reader), sim));
        }
        return results;
    }

    Task<IReadOnlyList<TaskMemoryResult>> ScoreBasedRerank(
        IReadOnlyList<TaskMemoryResult> candidates, int topK)
    {
        return Task.FromResult<IReadOnlyList<TaskMemoryResult>>(
            candidates.Take(topK).ToList());
    }

    async Task<IReadOnlyList<TaskMemoryResult>> LlmRerankAsync(
        string query, IReadOnlyList<TaskMemoryResult> candidates, int topK, CancellationToken ct)
    {
        return await ScoreBasedRerank(candidates, topK);
    }

    async Task<string> LlmRewriteAsync(
        string query, IReadOnlyList<TaskMemoryResult> memories, CancellationToken ct)
    {
        if (_llm is null)
            return DirectFormat(memories);
        try
        {
            var template = await PromptLoader.LoadAsync("WakeupRewrite.md", ct);
            var memText = DirectFormat(memories);
            var prompt = template
                .Replace("{query}", query)
                .Replace("{retrieved_memories}", memText);
            return await _llm.CompleteAsync(prompt, ct);
        }
        catch { return DirectFormat(memories); }
    }

    static string DirectFormat(IReadOnlyList<TaskMemoryResult> memories)
    {
        if (memories.Count == 0)
            return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Retrieved {memories.Count} memory(ies):");
        for (var i = 0; i < memories.Count; i++)
        {
            var m = memories[i].Memory;
            sb.AppendLine($"\nMemory {i + 1}:");
            sb.AppendLine($"When to use: {m.WhenToUse}");
            sb.AppendLine($"Content: {m.Content}");
        }
        return sb.ToString();
    }
}
