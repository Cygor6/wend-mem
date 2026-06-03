using Wendmem.Experiences;

namespace Wendmem.ToolsMemory;

public sealed class ToolMemoryDistiller
{
    readonly LlmService _llm;
    readonly ToolMemoryStorage _storage;
    public ToolMemoryDistiller(LlmService llm, ToolMemoryStorage storage) { _llm = llm; _storage = storage; }

    public async Task<ToolMemory?> SummarizeAsync(string wing, string toolName, int recentLimit, CancellationToken ct)
    {
        // Evaluate pending calls first
        await EvaluatePendingCallsAsync(wing, toolName, ct);

        var calls = await _storage.GetRecentCallsAsync(wing, toolName, recentLimit, ct);
        if (calls.Count < 3)
            return null;
        var stats = await _storage.ComputeStatisticsAsync(wing, toolName, recentLimit, ct);
        var template = await PromptLoader.LoadAsync("ToolMemorySummary.md", ct);
        var prompt = template.Replace("{tool_name}", toolName).Replace("{success_rate}", $"{stats.SuccessRate:P0}")
            .Replace("{total_calls}", stats.TotalCalls.ToString()).Replace("{avg_time}", $"{stats.AvgTimeSeconds:F2}s")
            .Replace("{avg_tokens}", stats.AvgTokenCost.ToString()).Replace("{call_history}", FormatCallHistory(calls));
        var guidelines = await _llm.CompleteAsync(prompt, ct);
        if (string.IsNullOrWhiteSpace(guidelines))
            return null;
        var now = DateTimeOffset.UtcNow;
        var memory = new ToolMemory(wing, toolName, guidelines.Trim(), stats.SuccessRate, _llm.ModelName, now, now);
        await _storage.UpsertGuidelinesAsync(memory, ct);
        // Mark calls as summarized
        await _storage.MarkCallsSummarizedAsync(wing, toolName, calls.Select(c => c.Id), ct);
        return memory;
    }

    public async Task EvaluatePendingCallsAsync(string wing, string toolName, CancellationToken ct)
    {
        var pending = await _storage.GetUnsummarizedCallsAsync(wing, toolName, ct);
        if (pending.Count == 0)
            return;
        var template = await PromptLoader.LoadAsync("ToolCallEvaluation.md", ct);
        foreach (var call in pending)
        {
            var prompt = template.Replace("{tool_name}", call.ToolName).Replace("{input}", call.InputJson).Replace("{output}", call.OutputJson);
            var verdict = await _llm.CompleteCallVerdictAsync(prompt, ct);
            if (verdict is null)
                continue;
            var binaryScore = verdict.Score >= 0.5f ? 1.0f : 0.0f;
            await _storage.UpdateCallEvaluationAsync(call.Id, binaryScore, verdict.Summary ?? "", ct);
        }
    }

    static string FormatCallHistory(IReadOnlyList<ToolCallResult> calls)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in calls)
        {
            sb.AppendLine($"- [{(c.Success ? "OK" : "FAIL")}] {c.CalledAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"  Input:  {Truncate(c.InputJson, 200)}");
            sb.AppendLine($"  Output: {Truncate(c.OutputJson, 200)}");
            sb.AppendLine($"  Cost: {c.TokenCost} tokens, {c.TimeSeconds:F2}s");
        }
        return sb.ToString();
    }
    static string Truncate(string s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "...";
}
