namespace Wendmem.Experiences;

public record TaskMemory(
    string Id,
    string Wing,
    string WhenToUse,
    string Content,
    float Score,
    string Author,
    string[] Keywords,
    string[] ToolsUsed,
    TaskMemorySource Source,
    int RetrievalCount,
    int UtilityCount,
    float[]? Embedding,
    DateTimeOffset TimeCreated,
    DateTimeOffset TimeModified,
    DateTimeOffset? LastUsedAt);

public enum TaskMemorySource { Success, Failure, Comparative, Reflection }

public record TaskMemoryResult(TaskMemory Memory, float SimilarityScore);

public static class TaskMemoryIds
{
    public static string Compute(string whenToUse, string content)
    {
        var combined = $"{whenToUse}\n{content}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(combined);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }
}
