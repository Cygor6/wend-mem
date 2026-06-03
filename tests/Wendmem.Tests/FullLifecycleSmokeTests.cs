using Wendmem.Experiences;
using Wendmem.Storage;
using Wendmem.ToolsMemory;

namespace Wendmem.Tests;

/// <summary>
/// End-to-end lifecycle: store memories → record outcomes → prune → verify state.
/// No LLM calls - purely storage + retrieval logic.
/// </summary>
public class FullLifecycleSmokeTests
{
    DuckDbConnectionFactory _factory = null!;
    string _dbPath = null!;
    TaskMemoryStorage _taskStorage = null!;
    ToolMemoryStorage _toolStorage = null!;

    [Before(Test)]
    public async Task InitializeAsync()
    {
        _dbPath = Path.GetTempFileName() + ".duckdb";
        _factory = new DuckDbConnectionFactory(_dbPath);

        _taskStorage = new TaskMemoryStorage(_factory);
        _toolStorage = new ToolMemoryStorage(_factory);

        await Task.CompletedTask;
    }

    [After(Test)]
    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Test]
    public async Task TaskMemory_Lifecycle_StoreRetrieveRecordPrune()
    {
        // 1. Store memories
        var emb = new float[512];
        for (var i = 0; i < 512; i++)
            emb[i] = 0.05f;

        var useful = MakeTaskMemory("Use pagination for large result sets", emb);
        var useless = MakeTaskMemory("Outdated pattern from v1", emb);
        await _taskStorage.AddAsync(useful, default);
        await _taskStorage.AddAsync(useless, default);

        await Assert.That(await _taskStorage.CountAsync("lifecycle-wing", default)).IsEqualTo(2);

        // 2. Record retrievals (simulate lookups)
        for (var i = 0; i < 8; i++)
            await _taskStorage.RecordRetrievalAsync([useful.Id, useless.Id], default);

        // 3. Record utility - only for the useful one
        await _taskStorage.RecordUtilityAsync([useful.Id], default);

        // 4. Prune low-utility memories
        var deleted = await _taskStorage.DeletePrunableAsync("lifecycle-wing", 5, 0.5f, default);

        // useful: utility=1, retrieval=8 → ratio=0.125 < 0.5 → also pruned
        // Both get pruned since neither meets 0.5 threshold with 8 retrievals
        await Assert.That(deleted).IsEqualTo(2);
        await Assert.That(await _taskStorage.CountAsync("lifecycle-wing", default)).IsEqualTo(0);
    }

    [Test]
    public async Task TaskMemory_Lifecycle_HighUtilitySurvives()
    {
        var emb = new float[512];
        var useful = MakeTaskMemory("Always use transactions for writes", emb);
        await _taskStorage.AddAsync(useful, default);

        // Many retrievals and high utility
        for (var i = 0; i < 6; i++)
            await _taskStorage.RecordRetrievalAsync([useful.Id], default);
        for (var i = 0; i < 5; i++)
            await _taskStorage.RecordUtilityAsync([useful.Id], default);

        // utility_ratio = 5/6 ~ 0.83 > 0.5 → survives
        var deleted = await _taskStorage.DeletePrunableAsync("lifecycle-wing", 5, 0.5f, default);
        await Assert.That(deleted).IsEqualTo(0);
        await Assert.That(await _taskStorage.CountAsync("lifecycle-wing", default)).IsEqualTo(1);
    }

    [Test]
    public async Task ToolMemory_Lifecycle_RecordEvaluateSummarizeGuidelines()
    {
        // 1. Record several tool calls
        for (var i = 0; i < 5; i++)
        {
            var call = new ToolCallResult(
                Id: ToolCallIds.Compute("my_tool", $"{{\"page\":{i}}}", DateTimeOffset.UtcNow),
                Wing: "lifecycle-wing", ToolName: "my_tool",
                InputJson: $"{{\"page\":{i}}}", OutputJson: """{"result":"ok"}""",
                Success: i < 4, Score: i < 4 ? 1.0f : 0.0f,
                Summary: null, TokenCost: 50, TimeSeconds: 0.3,
                IsSummarized: false, CalledAt: DateTimeOffset.UtcNow);
            await _toolStorage.RecordCallAsync(call, default);
        }

        // 2. Check statistics
        var stats = await _toolStorage.ComputeStatisticsAsync("lifecycle-wing", "my_tool", 10, default);
        await Assert.That(stats.TotalCalls).IsEqualTo(5);
        await Assert.That(stats.Successes).IsEqualTo(4);
        await Assert.That(stats.SuccessRate).IsEqualTo(0.8f).Within(0.1f);

        // 3. Evaluate calls (simulate binary scoring)
        var pending = await _toolStorage.GetUnsummarizedCallsAsync("lifecycle-wing", "my_tool", default);
        await Assert.That(pending).Count().IsEqualTo(5);
        foreach (var call in pending)
            await _toolStorage.UpdateCallEvaluationAsync(call.Id, call.Success ? 1.0f : 0.0f, call.Success ? "Good" : "Failed", default);

        // 4. Mark summarized
        await _toolStorage.MarkCallsSummarizedAsync("lifecycle-wing", "my_tool", pending.Select(c => c.Id), default);
        var unsummarized = await _toolStorage.GetUnsummarizedCallsAsync("lifecycle-wing", "my_tool", default);
        await Assert.That(unsummarized).IsEmpty();

        // 5. Upsert guidelines
        var now = DateTimeOffset.UtcNow;
        await _toolStorage.UpsertGuidelinesAsync(
            new ToolMemory("lifecycle-wing", "my_tool", "Use pagination; avoid page > 3", 0.8f, "test", now, now), default);

        var guidelines = await _toolStorage.GetGuidelinesAsync("lifecycle-wing", "my_tool", default);
        await Assert.That(guidelines).IsNotNull();
        await Assert.That(guidelines!.Guidelines).Contains("pagination");
    }

    static TaskMemory MakeTaskMemory(string content, float[]? embedding = null)
    {
        var when = $"When dealing with {content[..Math.Min(20, content.Length)]}";
        return new TaskMemory(
            Id: TaskMemoryIds.Compute(when, content),
            Wing: "lifecycle-wing",
            WhenToUse: when,
            Content: content,
            Score: 0.85f,
            Author: "lifecycle-test",
            Keywords: [],
            ToolsUsed: [],
            Source: TaskMemorySource.Success,
            RetrievalCount: 0,
            UtilityCount: 0,
            Embedding: embedding,
            TimeCreated: DateTimeOffset.UtcNow,
            TimeModified: DateTimeOffset.UtcNow,
            LastUsedAt: null);
    }
}
