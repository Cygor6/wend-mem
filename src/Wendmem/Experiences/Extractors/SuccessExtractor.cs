using Wendmem.Models;

namespace Wendmem.Experiences.Extractors;

public sealed class SuccessExtractor(LlmService llm)
{
    public async Task<IReadOnlyList<ExtractedMemory>> ExtractAsync(
        IReadOnlyList<Trajectory> successful, CancellationToken ct)
        => await ExtractAsync(successful, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ct);

    public async Task<IReadOnlyList<ExtractedMemory>> ExtractAsync(
        IReadOnlyList<Trajectory> successful, HashSet<string> survivingVocabulary, CancellationToken ct)
    {
        if (successful.Count == 0)
            return [];

        var template = await PromptLoader.LoadAsync("SuccessExtraction.md", ct);
        var vocab = survivingVocabulary.Count > 0
            ? string.Join(", ", survivingVocabulary.Take(30))
            : "(none yet)";
        var tasks = successful.Select(t => ExtractOneAsync(template, t, vocab, ct));
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    async Task<IReadOnlyList<ExtractedMemory>> ExtractOneAsync(
        string template, Trajectory traj, string vocab, CancellationToken ct)
    {
        var prompt = template
            .Replace("{query}", traj.TaskQuery)
            .Replace("{step_sequence}", FormatMessages(traj.Messages))
            .Replace("{context}", $"Tools available: {string.Join(", ", traj.ToolsUsed ?? [])}")
            .Replace("{surviving_vocabulary}", vocab);

        var raws = await llm.CompleteRawMemoryListAsync(prompt, ct);
        return raws.Where(r => RawMemoryDtoExt.IsValid(r)).Select(r => RawMemoryDtoExt.ToExtracted(r, TaskMemorySource.Success)).ToList();
    }

    static string FormatMessages(IReadOnlyList<TrajectoryMessage> messages) =>
        string.Join("\n", messages.Select(m => $"[{m.Role}] {m.Content}"));
}
