using Wendmem.Models;

namespace Wendmem.Experiences.Extractors;

public sealed class ComparativeExtractor(LlmService llm)
{
    public async Task<IReadOnlyList<ExtractedMemory>> ExtractAsync(
        IReadOnlyList<TrajectoryPair> pairs, CancellationToken ct)
        => await ExtractAsync(pairs, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ct);

    public async Task<IReadOnlyList<ExtractedMemory>> ExtractAsync(
        IReadOnlyList<TrajectoryPair> pairs, HashSet<string> survivingVocabulary, CancellationToken ct)
    {
        if (pairs.Count == 0)
            return [];

        var template = await PromptLoader.LoadAsync("ComparativeExtraction.md", ct);
        var vocab = survivingVocabulary.Count > 0
            ? string.Join(", ", survivingVocabulary.Take(30))
            : "(none yet)";
        var tasks = pairs.Select(p => ExtractOneAsync(template, p, vocab, ct));
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    async Task<IReadOnlyList<ExtractedMemory>> ExtractOneAsync(
        string template, TrajectoryPair pair, string vocab, CancellationToken ct)
    {
        var prompt = template
            .Replace("{higher_score}", pair.Higher.Score.ToString("F2"))
            .Replace("{lower_score}", pair.Lower.Score.ToString("F2"))
            .Replace("{higher_steps}", string.Join("\n", pair.Higher.Messages.Select(m => $"[{m.Role}] {m.Content}")))
            .Replace("{lower_steps}", string.Join("\n", pair.Lower.Messages.Select(m => $"[{m.Role}] {m.Content}")))
            .Replace("{surviving_vocabulary}", vocab);

        var raws = await llm.CompleteRawMemoryListAsync(prompt, ct);
        return raws.Where(r => RawMemoryDtoExt.IsValid(r)).Select(r => RawMemoryDtoExt.ToExtracted(r, TaskMemorySource.Comparative)).ToList();
    }
}
