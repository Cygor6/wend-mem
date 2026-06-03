using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Wendmem.Options;
using Wendmem.Serialization;

namespace Wendmem.Services;

/// <summary>
/// Classifies raw heuristic entities against content using the configured LLM backend.
/// Falls back to "concept" on any error - never blocks ingestion.
/// </summary>
sealed class EntityRefinementService(IChatClient chat, IOptions<LlmOptions> llmOpts)
{
    readonly IChatClient _chat = chat;
    readonly bool _enabled = llmOpts.Value.EntityRefinement is not { Enabled: false };

    /// <summary>
    /// Classify raw heuristic entities against a content string.
    /// Returns refined (name, type) pairs. Falls back to input on any error.
    /// </summary>
    public async Task<IReadOnlyList<(string Name, string Type)>> RefineAsync(
        string content,
        IReadOnlyList<string> candidateNames,
        CancellationToken ct)
    {
        if (!_enabled || candidateNames.Count == 0)
            return candidateNames.Select(n => (n, "concept")).ToList();

        var prompt = $"Given this text:\n---\n{content[..Math.Min(content.Length, 500)]}\n---\n" +
                     $"Classify each of these terms as one of: person, project, tool, concept\n" +
                     $"Terms: {string.Join(", ", candidateNames)}\n" +
                     "Reply ONLY with JSON array: [{\"name\":\"...\",\"type\":\"...\"}]";

        try
        {
            var response = await _chat.GetResponseAsync(prompt, cancellationToken: ct);
            var responseText = response.Text ?? "";

            // Extract JSON array from response
            var start = responseText.IndexOf('[');
            var end = responseText.LastIndexOf(']');
            if (start < 0 || end < 0)
                return Fallback(candidateNames);

            var json = responseText[start..(end + 1)];
            var arr = System.Text.Json.JsonSerializer.Deserialize(
                json, WendmemJsonContext.Default.ListEntityCandidate);

            return arr?.Select(e => (e.Name, e.Type)).ToList()
                   ?? Fallback(candidateNames);
        }
        catch
        {
            return Fallback(candidateNames);
        }
    }

    static IReadOnlyList<(string, string)> Fallback(IReadOnlyList<string> names)
        => names.Select(n => (n, "concept")).ToList();
}
