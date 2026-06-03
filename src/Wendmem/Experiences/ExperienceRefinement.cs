using Wendmem.Services;

namespace Wendmem.Experiences;

public sealed class ExperienceRefinement
{
    readonly LlmService _llm;
    readonly IEmbedder _embedder;
    readonly TaskMemoryStorage _storage;
    readonly Extractors.SuccessExtractor _success;
    readonly MemoryValidator _validator;
    readonly MemoryDeduplicator _deduplicator;
    readonly Options.ExperienceOptions _config;

    public ExperienceRefinement(
        LlmService llm, IEmbedder embedder, TaskMemoryStorage storage,
        Extractors.SuccessExtractor success, MemoryValidator validator,
        MemoryDeduplicator deduplicator, Options.ExperienceOptions config)
    {
        _llm = llm;
        _embedder = embedder;
        _storage = storage;
        _success = success;
        _validator = validator;
        _deduplicator = deduplicator;
        _config = config;
    }

    public Task<int> PruneAsync(string wing, CancellationToken ct) =>
        _storage.DeletePrunableAsync(wing, _config.PruneMinRetrievals, _config.PruneUtilityThreshold, ct);

    public async Task<IReadOnlyList<TaskMemory>> ReflectAsync(
        Trajectory failed, Trajectory? successfulRetry, string wing, CancellationToken ct)
    {
        if (successfulRetry is null || successfulRetry.Score < _config.SuccessScoreThreshold)
            return [];

        var extracted = await _success.ExtractAsync([successfulRetry], ct);
        if (extracted.Count == 0)
            return [];

        var marked = extracted.Select(e => e with { Source = TaskMemorySource.Reflection }).ToList();
        var validated = await _validator.ValidateAsync(marked, ct);
        var deduped = await _deduplicator.FilterAsync(validated, wing, ct);

        var now = DateTimeOffset.UtcNow;
        var persisted = new List<TaskMemory>();
        foreach (var (mem, embedding) in deduped)
        {
            var task = new TaskMemory(
                Id: TaskMemoryIds.Compute(mem.WhenToUse, mem.Content),
                Wing: wing, WhenToUse: mem.WhenToUse, Content: mem.Content,
                Score: mem.Score, Author: _llm.ModelName,
                Keywords: mem.Keywords, ToolsUsed: mem.ToolsUsed,
                Source: TaskMemorySource.Reflection,
                RetrievalCount: 0, UtilityCount: 0, Embedding: embedding,
                TimeCreated: now, TimeModified: now, LastUsedAt: null);
            await _storage.AddAsync(task, ct);
            persisted.Add(task);
        }
        return persisted;
    }
}
