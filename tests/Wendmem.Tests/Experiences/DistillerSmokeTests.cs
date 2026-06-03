using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using Wendmem.Experiences;
using Wendmem.Experiences.Extractors;
using Wendmem.Services;
using Wendmem.Storage;

namespace Wendmem.Tests.Experiences;

public class DistillerSmokeTests
{
    DuckDbConnectionFactory _factory = null!;
    string _dbPath = null!;
    TaskMemoryStorage _storage = null!;
    LlmService _llm = null!;
    ExperienceDistiller _distiller = null!;
    GemmaEmbedder _embedder = null!;

    [Before(Test)]
    public async Task InitializeAsync()
    {
        _dbPath = Path.GetTempFileName() + ".duckdb";
        _factory = new DuckDbConnectionFactory(_dbPath);

        _storage = new TaskMemoryStorage(_factory);

        _llm = CreateLlm()
            ?? throw new InvalidOperationException("Set MEMPALACE_LLM_ENDPOINT and MEMPALACE_LLM_MODEL");

        var repoRoot = FindRepoRoot();
        var modelPath = Environment.GetEnvironmentVariable("WENDMEM_MODEL_PATH") ?? Path.Combine(repoRoot, "models", "model.onnx");
        var tokenizerPath = Environment.GetEnvironmentVariable("WENDMEM_TOKENIZER_PATH") ?? Path.Combine(repoRoot, "models", "tokenizer.model");
        _embedder = new GemmaEmbedder(modelPath, tokenizerPath);

        var config = new Options.ExperienceOptions { UseSimpleFlow = false };

        var success = new SuccessExtractor(_llm);
        var failure = new FailureExtractor(_llm);
        var comparative = new ComparativeExtractor(_llm);
        var validator = new MemoryValidator(_llm, config.ValidationMinScore);
        var dedup = new MemoryDeduplicator(_storage, _embedder, config.DedupSimilarityThreshold);

        _distiller = new ExperienceDistiller(_llm, _embedder, _storage,
            success, failure, comparative, validator, dedup, config);
    }

    [After(Test)]
    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        File.Delete(_dbPath);
    }

    [Test]
    [Skip("Requires MEMPALACE_LLM_ENDPOINT and MEMPALACE_LLM_MODEL environment variables")]
    public async Task DistillAsync_ProducesValidatedMemories()
    {
        var trajectories = new[]
        {
            new Trajectory(
                TaskQuery: "Place a market order for AAPL",
                Messages:  [new("user", "buy 10 shares AAPL at market"),
                            new("assistant", "called get_stock_info(AAPL); price=180"),
                            new("assistant", "called place_order(AAPL, 10, 180)")],
                Score: 1.0f,
                ToolsUsed: ["get_stock_info", "place_order"]),
            new Trajectory(
                TaskQuery: "Place a market order for AAPL",
                Messages:  [new("user", "buy 10 shares AAPL at market"),
                            new("assistant", "called place_order(AAPL, 10, 999)")],
                Score: 0.0f,
                ToolsUsed: ["place_order"])
        };

        var result = await _distiller.DistillAsync(trajectories, "test-wing", default);

        await Assert.That(result).IsNotEmpty();
        foreach (var m in result)
        {
            await Assert.That(string.IsNullOrWhiteSpace(m.WhenToUse)).IsFalse();
            await Assert.That(string.IsNullOrWhiteSpace(m.Content)).IsFalse();
            await Assert.That(m.Score).IsBetween(0.0f, 1.0f);
            await Assert.That(m.Embedding).IsNotNull();
            await Assert.That(m.Embedding!.Length).IsEqualTo(512);
        }

        var stored = await _storage.CountAsync("test-wing", default);
        await Assert.That(stored).IsGreaterThan(0);
    }

    static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "wendmem.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    static LlmService? CreateLlm()
    {
        var endpoint = Environment.GetEnvironmentVariable("MEMPALACE_LLM_ENDPOINT");
        var model = Environment.GetEnvironmentVariable("MEMPALACE_LLM_MODEL");
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(model))
            return null;
        var key = Environment.GetEnvironmentVariable("MEMPALACE_LLM_KEY") ?? "ollama";
        var client = new OpenAIClient(
            new ApiKeyCredential(key),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint + "/v1") })
            .GetChatClient(model).AsIChatClient();
        return new LlmService(client, model);
    }
}
