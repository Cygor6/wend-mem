using Wendmem.Experiences;
using Wendmem.Storage;

namespace Wendmem.Tests.Experiences;

public class RetrievalSmokeTests
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
        File.Delete(_dbPath);
    }

    [Test]
    public async Task FindNearDuplicates_ReturnsMatchingEmbeddings()
    {
        var emb = new float[512];
        for (var i = 0; i < 512; i++)
            emb[i] = 0.01f;
        var mem = MakeMemory("search test", emb);
        await _storage.AddAsync(mem, default);

        var dups = await _storage.FindNearDuplicatesAsync("test-wing", emb, 0.5f, default);
        await Assert.That(dups).Count().IsEqualTo(1);
    }

    [Test]
    public async Task FindNearDuplicates_EmptyWing_ReturnsEmpty()
    {
        var emb = new float[512];
        var dups = await _storage.FindNearDuplicatesAsync("no-such-wing", emb, 0.5f, default);
        await Assert.That(dups).IsEmpty();
    }

    [Test]
    public async Task DirectFormat_ReturnsNonEmptyForMemories()
    {
        // Insert without embedding
        var mem = MakeMemory("format test");
        await _storage.AddAsync(mem, default);
        var list = await _storage.ListByWingAsync("test-wing", 10, default);
        await Assert.That(list).Count().IsEqualTo(1);
        await Assert.That(list[0].Content).IsEqualTo("format test");
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
            Keywords: ["test"],
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
