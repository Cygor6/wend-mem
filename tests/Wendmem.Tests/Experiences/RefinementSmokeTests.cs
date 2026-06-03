using Wendmem.Experiences;
using Wendmem.Storage;

namespace Wendmem.Tests.Experiences;

public class RefinementSmokeTests
{
    DuckDbConnectionFactory _factory = null!;
    string _dbPath = null!;
    TaskMemoryStorage _storage = null!;

    [Before(Test)]
    public async Task InitializeAsync()
    {
        _dbPath = Path.GetTempFileName() + ".duckdb";
        _factory = new DuckDbConnectionFactory(_dbPath);
        _storage = new TaskMemoryStorage(_factory);
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
    public async Task DeletePrunableAsync_RemovesLowUtilityMemories()
    {
        // Insert a memory with high retrieval but zero utility
        var mem = MakeMemory("prunable candidate");
        await _storage.AddAsync(mem, default);

        // Simulate many retrievals but no utility
        for (var i = 0; i < 10; i++)
            await _storage.RecordRetrievalAsync([mem.Id], default);

        var countBefore = await _storage.CountAsync("test-wing", default);
        await Assert.That(countBefore).IsEqualTo(1);

        // Prune with minRetrievals=5 and utilityThreshold=0.5
        var deleted = await _storage.DeletePrunableAsync("test-wing", 5, 0.5f, default);
        // This memory has retrieval_count=10 and utility_count=0,
        // utility_ratio = 0/10 = 0 < 0.5 — should be pruned
        await Assert.That(deleted).IsEqualTo(1);

        var countAfter = await _storage.CountAsync("test-wing", default);
        await Assert.That(countAfter).IsEqualTo(0);
    }

    [Test]
    public async Task DeletePrunableAsync_KeepsHighUtilityMemories()
    {
        var mem = MakeMemory("useful memory");
        await _storage.AddAsync(mem, default);

        // Simulate retrievals AND utility
        for (var i = 0; i < 6; i++)
            await _storage.RecordRetrievalAsync([mem.Id], default);
        await _storage.RecordUtilityAsync([mem.Id], default);

        // utility_ratio = 1/6 ~ 0.17 - below 0.5, so this WILL be pruned
        // Let's add more utility to keep it above threshold
        for (var i = 0; i < 3; i++)
            await _storage.RecordUtilityAsync([mem.Id], default);

        // Now utility_ratio = 4/6 ~ 0.67 > 0.5 — kept
        var deleted = await _storage.DeletePrunableAsync("test-wing", 5, 0.5f, default);
        await Assert.That(deleted).IsEqualTo(0);

        var countAfter = await _storage.CountAsync("test-wing", default);
        await Assert.That(countAfter).IsEqualTo(1);
    }

    [Test]
    public async Task DeletePrunableAsync_SkipsLowRetrievalMemories()
    {
        // Memory with low retrieval count should never be pruned
        var mem = MakeMemory("rare memory");
        await _storage.AddAsync(mem, default);

        // Only 2 retrievals - below minRetrievals=5
        await _storage.RecordRetrievalAsync([mem.Id], default);
        await _storage.RecordRetrievalAsync([mem.Id], default);

        var deleted = await _storage.DeletePrunableAsync("test-wing", 5, 0.5f, default);
        await Assert.That(deleted).IsEqualTo(0);
    }

    static TaskMemory MakeMemory(string content, float[]? embedding = null)
    {
        var when = "When the test scenario occurs";
        return new TaskMemory(
            Id: TaskMemoryIds.Compute(when, content),
            Wing: "test-wing",
            WhenToUse: when,
            Content: content,
            Score: 0.85f,
            Author: "test-author",
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
