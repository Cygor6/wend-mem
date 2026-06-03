using DuckDB.NET.Data;
using Microsoft.Extensions.Caching.Memory;
using Wendmem.Services;
using Wendmem.Storage;

namespace Wendmem.Tests;

public class V1DecayScoringVerify
{
    private static (DuckDbConnectionFactory factory, DrawerStorage storage, FakeEmbedderVec embedder) Make()
    {
        var dbPath = Path.GetTempFileName() + ".duckdb";
        var factory = new DuckDbConnectionFactory(dbPath);
        var embedder = new FakeEmbedderVec();
        var closets = new ClosetStorage(factory);
        var aaak = new AaakDialect(new Dictionary<string, string>());
        var kg = new KnowledgeGraph(factory);
        var entityIndex = new EntityIndexService(factory);
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 500 });
        var storage = new DrawerStorage(
            factory, embedder, closets, aaak, entityIndex, kg,
            cache, new PalaceConfig { AdmissionEnabled = false }, null);
        return (factory, storage, embedder);
    }

    [Test]
    public async Task SearchedFiveTimes_HigherScore_ThanSearchedOnce()
    {
        var (factory, storage, embedder) = Make();
        try
        {
            var text1 = new string('A', 200);
            var text2 = new string('B', 200);
            var r1 = await storage.AddDrawerAsync(text1, "test", "room", null, null);
            var r2 = await storage.AddDrawerAsync(text2, "test", "room", null, null);

            // Access drawer1 five times, drawer2 once
            for (int i = 0; i < 5; i++)
                storage.RecordAccess([r1.Id!]);
            storage.RecordAccess([r2.Id!]);
            await Task.Delay(500);

            var vec = await embedder.EmbedAsync("test");
            var results = await storage.SearchAsync(vec, "test", null, 10);

            var hit1 = results.FirstOrDefault(r => r.Drawer.Id == r1.Id);
            var hit2 = results.FirstOrDefault(r => r.Drawer.Id == r2.Id);

            await Assert.That(hit1).IsNotNull();
            await Assert.That(hit2).IsNotNull();
            await Assert.That(hit1!.Score).IsGreaterThan(hit2!.Score);
        }
        finally { factory.Dispose(); }
    }

    [Test]
    public async Task Stale100Days_ScoresAbout83Percent()
    {
        var (factory, storage, embedder) = Make();
        try
        {
            var added = await storage.AddDrawerAsync(new string('C', 200), "test", "room", null, null);

            // Set last_accessed_at = 100 days ago
            await factory.ExecuteWriteAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE drawers SET last_accessed_at = $dt, access_count = 1 WHERE id = $id";
                cmd.Parameters.Add(new DuckDBParameter("dt", DateTime.UtcNow.AddDays(-100)));
                cmd.Parameters.Add(new DuckDBParameter("id", added.Id!));
                await cmd.ExecuteNonQueryAsync();
            });

            var fresh = await storage.AddDrawerAsync(new string('D', 200), "test", "room", null, null);
            var vec = await embedder.EmbedAsync("test");
            var results = await storage.SearchAsync(vec, "test", null, 10);

            var stale = results.FirstOrDefault(r => r.Drawer.Id == added.Id);
            var freshHit = results.FirstOrDefault(r => r.Drawer.Id == fresh.Id);

            await Assert.That(stale).IsNotNull();
            await Assert.That(freshHit).IsNotNull();

            // Decay 100d: 1/(1+0.002*100) ≈ 0.833, boost: 1+0.1*log10(2) ≈ 1.03
            var ratio = stale!.Score / freshHit!.Score;
            await Assert.That(ratio).IsGreaterThan(0.78f);
            await Assert.That(ratio).IsLessThan(0.90f);
        }
        finally { factory.Dispose(); }
    }

    private sealed class FakeEmbedderVec : IEmbedder
    {
        public int EmbeddingDimension => 512;
        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => new(Enumerable.Repeat(0.1f, 512).ToArray());

        public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult(texts.Select(_ => Enumerable.Repeat(0.1f, 512).ToArray()).ToArray());
    }
}
