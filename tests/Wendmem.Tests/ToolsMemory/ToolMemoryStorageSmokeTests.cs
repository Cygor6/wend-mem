using Wendmem.Storage;
using Wendmem.ToolsMemory;

namespace Wendmem.Tests.ToolsMemory;

public class ToolMemoryStorageSmokeTests
{
    DuckDbConnectionFactory _factory = null!;
    string _dbPath = null!;
    ToolMemoryStorage _storage = null!;

    [Before(Test)]
    public async Task InitializeAsync()
    {
        _dbPath = Path.GetTempFileName() + ".duckdb";
        _factory = new DuckDbConnectionFactory(_dbPath);
        _storage = new ToolMemoryStorage(_factory);
    }

    [After(Test)]
    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Test]
    public async Task RecordCallAsync_And_GetRecentCalls()
    {
        var call = MakeCall("add_memory", success: true);
        await _storage.RecordCallAsync(call, default);

        var calls = await _storage.GetRecentCallsAsync("test-wing", "add_memory", 10, default);
        await Assert.That(calls).Count().IsEqualTo(1);
        await Assert.That(calls[0].Id).IsEqualTo(call.Id);
        await Assert.That(calls[0].Success).IsTrue();
    }

    [Test]
    public async Task RecordCallAsync_IsIdempotent()
    {
        var call = MakeCall("add_memory", success: true);
        await _storage.RecordCallAsync(call, default);
        await _storage.RecordCallAsync(call, default);

        var calls = await _storage.GetRecentCallsAsync("test-wing", "add_memory", 10, default);
        await Assert.That(calls).Count().IsEqualTo(1);
    }

    [Test]
    public async Task UpdateCallEvaluation_UpdatesScoreAndSummary()
    {
        var call = MakeCall("search_keyword", success: false);
        await _storage.RecordCallAsync(call, default);

        await _storage.UpdateCallEvaluationAsync(call.Id, 1.0f, "Actually succeeded", default);

        var calls = await _storage.GetRecentCallsAsync("test-wing", "search_keyword", 10, default);
        await Assert.That(calls).Count().IsEqualTo(1);
        await Assert.That(calls[0].Score).IsEqualTo(1.0f);
        await Assert.That(calls[0].Summary).IsEqualTo("Actually succeeded");
    }

    [Test]
    public async Task MarkCallsSummarized_SetsFlag()
    {
        var call = MakeCall("mine_file", success: true);
        await _storage.RecordCallAsync(call, default);

        var unsummarized = await _storage.GetUnsummarizedCallsAsync("test-wing", "mine_file", default);
        await Assert.That(unsummarized).Count().IsEqualTo(1);

        await _storage.MarkCallsSummarizedAsync("test-wing", "mine_file", [call.Id], default);

        var after = await _storage.GetUnsummarizedCallsAsync("test-wing", "mine_file", default);
        await Assert.That(after).IsEmpty();

        var all = await _storage.GetRecentCallsAsync("test-wing", "mine_file", 10, default);
        await Assert.That(all[0].IsSummarized).IsTrue();
    }

    [Test]
    public async Task ComputeStatistics_ReturnsCorrectCounts()
    {
        for (var i = 0; i < 5; i++)
        {
            var call = MakeCall("stats_tool", success: i < 3, tokenCost: 100 + i, timeSeconds: 0.5 + i * 0.1);
            await _storage.RecordCallAsync(call, default);
        }

        var stats = await _storage.ComputeStatisticsAsync("test-wing", "stats_tool", 10, default);
        await Assert.That(stats.TotalCalls).IsEqualTo(5);
        await Assert.That(stats.Successes).IsEqualTo(3);
        await Assert.That(stats.SuccessRate).IsEqualTo(0.6f).Within(0.1f);
    }

    [Test]
    public async Task UpsertGuidelines_And_GetGuidelines()
    {
        var now = DateTimeOffset.UtcNow;
        var mem = new ToolMemory("test-wing", "my_tool", "Use sparingly", 0.9f, "test", now, now);
        await _storage.UpsertGuidelinesAsync(mem, default);

        var fetched = await _storage.GetGuidelinesAsync("test-wing", "my_tool", default);
        await Assert.That(fetched).IsNotNull();
        await Assert.That(fetched!.Guidelines).IsEqualTo("Use sparingly");
        await Assert.That(fetched.Score).IsEqualTo(0.9f);
    }

    [Test]
    public async Task UpsertGuidelines_UpdatesExisting()
    {
        var now = DateTimeOffset.UtcNow;
        var v1 = new ToolMemory("test-wing", "upsert_tool", "Version 1", 0.5f, "test", now, now);
        await _storage.UpsertGuidelinesAsync(v1, default);

        var v2 = new ToolMemory("test-wing", "upsert_tool", "Version 2", 0.8f, "test", now, now);
        await _storage.UpsertGuidelinesAsync(v2, default);

        var fetched = await _storage.GetGuidelinesAsync("test-wing", "upsert_tool", default);
        await Assert.That(fetched).IsNotNull();
        await Assert.That(fetched!.Guidelines).IsEqualTo("Version 2");
        await Assert.That(fetched.Score).IsEqualTo(0.8f);
    }

    [Test]
    public async Task ToolCallIds_AreDeterministic()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var id1 = ToolCallIds.Compute("add_memory", "{}", at);
        var id2 = ToolCallIds.Compute("add_memory", "{}", at);
        var id3 = ToolCallIds.Compute("add_memory", "{other}", at);
        await Assert.That(id1).IsEqualTo(id2);
        await Assert.That(id1).IsNotEqualTo(id3);
    }

    static ToolCallResult MakeCall(string tool, bool success, int tokenCost = 0, double timeSeconds = 0.0)
    {
        var now = DateTimeOffset.UtcNow;
        return new ToolCallResult(
            Id: ToolCallIds.Compute(tool, "{}", now),
            Wing: "test-wing",
            ToolName: tool,
            InputJson: "{}",
            OutputJson: """{"ok":true}""",
            Success: success,
            Score: success ? 1.0f : 0.0f,
            Summary: null,
            TokenCost: tokenCost,
            TimeSeconds: timeSeconds,
            IsSummarized: false,
            CalledAt: now);
    }
}
