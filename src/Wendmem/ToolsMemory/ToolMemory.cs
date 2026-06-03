namespace Wendmem.ToolsMemory;

public record ToolMemory(
    string Wing, string ToolName, string Guidelines, float Score, string Author,
    DateTimeOffset TimeCreated, DateTimeOffset TimeModified);

public record ToolCallResult(
    string Id, string Wing, string ToolName, string InputJson, string OutputJson,
    bool Success, float Score, string? Summary, int TokenCost, double TimeSeconds,
    bool IsSummarized, DateTimeOffset CalledAt);

public record ToolMemoryStatistics(
    string ToolName, int TotalCalls, int Successes, float SuccessRate,
    double AvgTimeSeconds, int AvgTokenCost);

public static class ToolCallIds
{
    public static string Compute(string toolName, string inputJson, DateTimeOffset calledAt)
    {
        var combined = $"{toolName}|{inputJson}|{calledAt:O}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }
}
