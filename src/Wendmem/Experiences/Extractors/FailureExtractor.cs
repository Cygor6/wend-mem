using Wendmem.Models;

namespace Wendmem.Experiences.Extractors;

public sealed class FailureExtractor(LlmService llm)
{
    public async Task<IReadOnlyList<ExtractedMemory>> ExtractAsync(
        IReadOnlyList<Trajectory> failed, CancellationToken ct)
        => await ExtractAsync(failed, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ct);

    public async Task<IReadOnlyList<ExtractedMemory>> ExtractAsync(
        IReadOnlyList<Trajectory> failed, HashSet<string> survivingVocabulary, CancellationToken ct)
    {
        if (failed.Count == 0)
            return [];

        var template = await PromptLoader.LoadAsync("FailureExtraction.md", ct);
        var vocab = survivingVocabulary.Count > 0
            ? string.Join(", ", survivingVocabulary.Take(30))
            : "(none yet)";
        var tasks = failed.Select(t => ExtractOneAsync(template, t, vocab, ct));
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    async Task<IReadOnlyList<ExtractedMemory>> ExtractOneAsync(
        string template, Trajectory traj, string vocab, CancellationToken ct)
    {
        var prompt = template
            .Replace("{query}", traj.TaskQuery)
            .Replace("{step_sequence}", string.Join("\n", traj.Messages.Select(m => $"[{m.Role}] {m.Content}")))
            .Replace("{context}", $"Tools available: {string.Join(", ", traj.ToolsUsed ?? [])}")
            .Replace("{surviving_vocabulary}", vocab);

        var raws = await llm.CompleteRawMemoryListAsync(prompt, ct);
        return raws.Where(r => RawMemoryDtoExt.IsValid(r)).Select(r => RawMemoryDtoExt.ToExtracted(r, TaskMemorySource.Failure)).ToList();
    }
}
