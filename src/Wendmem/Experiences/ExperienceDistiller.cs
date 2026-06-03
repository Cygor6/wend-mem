using Wendmem.Services;

namespace Wendmem.Experiences;

public sealed class ExperienceDistiller
{
    readonly LlmService _llm;
    readonly IEmbedder _embedder;
    readonly TaskMemoryStorage _storage;
    readonly Extractors.SuccessExtractor _success;
    readonly Extractors.FailureExtractor _failure;
    readonly Extractors.ComparativeExtractor _comparative;
    readonly MemoryValidator _validator;
    readonly MemoryDeduplicator _deduplicator;
    readonly Options.ExperienceOptions _config;

    public ExperienceDistiller(
        LlmService llm,
        IEmbedder embedder,
        TaskMemoryStorage storage,
        Extractors.SuccessExtractor success,
        Extractors.FailureExtractor failure,
        Extractors.ComparativeExtractor comparative,
        MemoryValidator validator,
        MemoryDeduplicator deduplicator,
        Options.ExperienceOptions config)
    {
        _llm = llm;
        _embedder = embedder;
        _storage = storage;
        _success = success;
        _failure = failure;
        _comparative = comparative;
        _validator = validator;
        _deduplicator = deduplicator;
        _config = config;
    }

    public async Task<IReadOnlyList<TaskMemory>> DistillAsync(
        IReadOnlyList<Trajectory> trajectories,
        string wing,
        CancellationToken ct)
    {
        var partition = TrajectoryPreprocessor.Partition(trajectories, _config.SuccessScoreThreshold);

        var survivingVocab = await LoadSurvivingVocabularyAsync(wing, ct);

        IReadOnlyList<Extractors.ExtractedMemory> successMems, failureMems, comparativeMems;

        if (_config.UseSimpleFlow)
        {
            successMems = await _success.ExtractAsync(partition.Successful, survivingVocab, ct);
            failureMems = [];
            comparativeMems = [];
        }
        else
        {
            var successTask = _success.ExtractAsync(partition.Successful, survivingVocab, ct);
            var failureTask = _failure.ExtractAsync(partition.Failed, survivingVocab, ct);
            var comparativeTask = _comparative.ExtractAsync(partition.ComparativePairs, survivingVocab, ct);
            await Task.WhenAll(successTask, failureTask, comparativeTask);
            successMems = await successTask;
            failureMems = await failureTask;
            comparativeMems = await comparativeTask;
        }

        var candidates = successMems.Concat(failureMems).Concat(comparativeMems).ToList();
        if (candidates.Count == 0)
            return [];

        var validated = await _validator.ValidateAsync(candidates, ct);

        var deduped = await _deduplicator.FilterAsync(validated, wing, ct);

        var now = DateTimeOffset.UtcNow;
        var persisted = new List<TaskMemory>();
        foreach (var (mem, embedding) in deduped)
        {
            var task = new TaskMemory(
                Id: TaskMemoryIds.Compute(mem.WhenToUse, mem.Content),
                Wing: wing,
                WhenToUse: mem.WhenToUse,
                Content: mem.Content,
                Score: mem.Score,
                Author: _llm.ModelName,
                Keywords: mem.Keywords,
                ToolsUsed: mem.ToolsUsed,
                Source: mem.Source,
                RetrievalCount: 0,
                UtilityCount: 0,
                Embedding: embedding,
                TimeCreated: now,
                TimeModified: now,
                LastUsedAt: null);

            await _storage.AddAsync(task, ct);
            persisted.Add(task);
        }
        return persisted;
    }
    /// <summary>
    /// Loads keywords from existing high-utility task memories in this wing.
    /// These "surviving vocabulary" terms are preferred during extraction
    /// to maintain terminology consistency across distillation cycles.
    /// Only keywords from memories with utility_count >= 2 are included,
    /// ensuring only proven terminology survives.
    /// </summary>
    async Task<HashSet<string>> LoadSurvivingVocabularyAsync(string wing, CancellationToken ct)
    {
        var existing = await _storage.ListByWingAsync(wing, limit: 50, ct);
        var vocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mem in existing)
        {
            if (mem.UtilityCount < 2)
                continue;
            foreach (var kw in mem.Keywords)
                vocab.Add(kw);
        }

        return vocab;
    }
}
