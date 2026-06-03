using Wendmem.Experiences;
using Wendmem.Storage;

namespace Wendmem.Tests.Experiences;

public class TaskMemoryStorageSmokeTests
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
    public async Task AddAsync_IsIdempotent()
    {
        var mem = MakeMemory("Use stock_info before placing market orders");
        var id1 = await _storage.AddAsync(mem, default);
        var id2 = await _storage.AddAsync(mem, default);

        await Assert.That(id1).IsEqualTo(id2);
        await Assert.That(await _storage.CountAsync("test-wing", default)).IsEqualTo(1);
    }

    [Test]
    public async Task GetAsync_ReturnsStoredMemory()
    {
        var mem = MakeMemory("Test content for retrieval");
        await _storage.AddAsync(mem, default);

        var fetched = await _storage.GetAsync(mem.Id, default);
        await Assert.That(fetched).IsNotNull();
        await Assert.That(fetched!.WhenToUse).IsEqualTo(mem.WhenToUse);
        await Assert.That(fetched.Score).IsEqualTo(mem.Score);
        await Assert.That(fetched.Source).IsEqualTo(TaskMemorySource.Success);
    }

    [Test]
    public async Task RecordRetrieval_IncrementsCounter()
    {
        var mem = MakeMemory("counter test");
        await _storage.AddAsync(mem, default);

        await _storage.RecordRetrievalAsync([mem.Id], default);
        await _storage.RecordRetrievalAsync([mem.Id], default);

        var fetched = await _storage.GetAsync(mem.Id, default);
        await Assert.That(fetched!.RetrievalCount).IsEqualTo(2);
        await Assert.That(fetched.LastUsedAt).IsNotNull();
    }

    [Test]
    public async Task FindNearDuplicates_ReturnsMatches()
    {
        var emb = new float[512];
        for (var i = 0; i < emb.Length; i++)
            emb[i] = 0.1f;

        var mem = MakeMemory("dedup test", emb);
        await _storage.AddAsync(mem, default);

        // Same vector -> similarity 1.0
        var dups = await _storage.FindNearDuplicatesAsync("test-wing", emb, 0.9f, default);
        await Assert.That(dups).Count().IsEqualTo(1);
        await Assert.That(dups[0].SimilarityScore).IsEqualTo(1.0f).Within(0.001f);
    }

    [Test]
    public async Task DeterministicId_ProducesSameId()
    {
        var id1 = TaskMemoryIds.Compute("when X happens", "do Y");
        var id2 = TaskMemoryIds.Compute("when X happens", "do Y");
        var id3 = TaskMemoryIds.Compute("when X happens", "do Z");
        await Assert.That(id1).IsEqualTo(id2);
        await Assert.That(id1).IsNotEqualTo(id3);
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
            Keywords: ["test", "smoke"],
            ToolsUsed: ["mock_tool"],
            Source: TaskMemorySource.Success,
            RetrievalCount: 0,
            UtilityCount: 0,
            Embedding: embedding,
            TimeCreated: DateTimeOffset.UtcNow,
            TimeModified: DateTimeOffset.UtcNow,
            LastUsedAt: null);
    }
}
