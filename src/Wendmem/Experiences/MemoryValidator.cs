namespace Wendmem.Experiences;

public sealed class MemoryValidator(LlmService llm, float minScore = 0.5f)
{
    public async Task<IReadOnlyList<Extractors.ExtractedMemory>> ValidateAsync(
        IReadOnlyList<Extractors.ExtractedMemory> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0)
            return [];

        var template = await PromptLoader.LoadAsync("Validation.md", ct);
        var tasks = candidates.Select(c => ValidateOneAsync(template, c, ct));
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r.passed).Select(r => r.memory).ToList();
    }

    async Task<(Extractors.ExtractedMemory memory, bool passed)> ValidateOneAsync(
        string template, Extractors.ExtractedMemory mem, CancellationToken ct)
    {
        var prompt = template
            .Replace("{when_to_use}", mem.WhenToUse)
            .Replace("{content}", mem.Content);

        var verdict = await llm.CompleteValidationVerdictAsync(prompt, ct);
        if (verdict is null)
            return (mem, false);

        var passed = verdict.IsValid && verdict.Score >= minScore;
        var adjusted = mem with { Score = (mem.Score + verdict.Score) / 2.0f };
        return (adjusted, passed);
    }
}
