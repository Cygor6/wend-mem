using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using Wendmem.Experiences;

namespace Wendmem.Tests.Experiences;

/// <summary>
/// Integration tests that require a running LLM endpoint.
/// Set MEMPALACE_LLM_ENDPOINT and MEMPALACE_LLM_MODEL (and optionally MEMPALACE_LLM_KEY)
/// to run these tests. Otherwise they are skipped.
/// </summary>
public class LlmServiceSmokeTests
{
    static LlmService? TryCreate()
    {
        var endpoint = Environment.GetEnvironmentVariable("MEMPALACE_LLM_ENDPOINT");
        var model = Environment.GetEnvironmentVariable("MEMPALACE_LLM_MODEL");
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(model))
            return null;

        var key = Environment.GetEnvironmentVariable("MEMPALACE_LLM_KEY") ?? "ollama";
        var client = new OpenAIClient(
            new ApiKeyCredential(key),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
            .GetChatClient(model).AsIChatClient();
        return new LlmService(client, model);
    }

    [Test]
    [Skip("Requires MEMPALACE_LLM_ENDPOINT and MEMPALACE_LLM_MODEL environment variables")]
    public async Task ChatClient_RespondsWithText()
    {
        var llm = TryCreate()!;
        using var service = llm;

        var reply = await service.CompleteAsync(
            "Respond with exactly the single word: pong",
            CancellationToken.None);

        await Assert.That(string.IsNullOrWhiteSpace(reply)).IsFalse();
        await Assert.That(reply).Contains("pong");
    }

    [Test]
    [Skip("Requires MEMPALACE_LLM_ENDPOINT and MEMPALACE_LLM_MODEL environment variables")]
    public async Task CompleteRawMemoryListAsync_ParsesJson()
    {
        var llm = TryCreate()!;
        using var service = llm;

        var prompt = """
            Respond with ONLY this JSON array - no prose, no fences:
            [{"when_to_use":"testing","content":"test memory","keywords":["test"],"score":0.9,"tools_used":[]}]
            """;

        var result = await service.CompleteRawMemoryListAsync(prompt, CancellationToken.None);
        await Assert.That(result).IsNotEmpty();
        await Assert.That(result[0].WhenToUse).IsEqualTo("testing");
        await Assert.That(result[0].Content).IsEqualTo("test memory");
    }
}
