using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Wendmem.Services;
using Wendmem.Storage;

namespace Wendmem.Tests;

/// <summary>
/// Feature 2 done-criteria: admission control on AddDrawerAsync.
/// </summary>
public class V1AdmissionVerify
{
    private static string TempDb() => Path.GetTempFileName() + ".duckdb";

    private static (DuckDbConnectionFactory factory, DrawerStorage storage, ActivityLog activityLog)
        Setup(string dbPath, PalaceConfig? cfg = null)
    {
        cfg ??= new PalaceConfig { AdmissionEnabled = true, AdmissionDuplicateThreshold = 0.97f };
        var factory = new DuckDbConnectionFactory(dbPath);
        var activityLog = new ActivityLog(factory, NullLogger<ActivityLog>.Instance);
        var closets = new ClosetStorage(factory);
        var aaak = new AaakDialect(new Dictionary<string, string>());
        var entityIndex = new EntityIndexService(factory);
        var kg = new KnowledgeGraph(factory);
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 500 });
        var storage = new DrawerStorage(
            factory, new FakeEmbedderVec(), closets, aaak, entityIndex, kg, cache, cfg, activityLog);
        return (factory, storage, activityLog);
    }

    [Test]
    public async Task ShortText_RejectedOnLength()
    {
        var db = TempDb();
        try
        {
            var (_, storage, _) = Setup(db);

            var result = await storage.AddDrawerAsync("TODO: fix", "test", "room",
                source: null, sourceMtime: null, ct: default);

            await Assert.That(result.Admitted).IsFalse();
            await Assert.That(result.Reason).IsEqualTo("content too short");
        }
        finally { File.Delete(db); }
    }

    [Test]
    public async Task NearDuplicate_SecondRejected_WithMatchedId()
    {
        var db = TempDb();
        try
        {
            var (_, storage, _) = Setup(db);

            // First drawer ~200 chars — admitted
            var text1 = new string('A', 200);
            var r1 = await storage.AddDrawerAsync(text1, "test", "room",
                source: null, sourceMtime: null, ct: default);
            await Assert.That(r1.Admitted).IsTrue();
            await Assert.That(r1.Id).IsNotNull();

            // Identical second drawer → cosine = 1.0 ≥ 0.97 → rejected
            var text2 = new string('A', 200);
            var r2 = await storage.AddDrawerAsync(text2, "test", "room",
                source: null, sourceMtime: null, ct: default);

            await Assert.That(r2.Admitted).IsFalse();
            await Assert.That(r2.Reason).IsEqualTo("near_duplicate");
            await Assert.That(r2.MatchedId).IsEqualTo(r1.Id);
        }
        finally { File.Delete(db); }
    }

    [Test]
    public async Task ActivityLog_ShowsRejections()
    {
        var db = TempDb();
        try
        {
            var (_, storage, activityLog) = Setup(db);

            // Trigger a short-text rejection
            await storage.AddDrawerAsync("short", "test", "room",
                source: null, sourceMtime: null, ct: default);
            await Task.Delay(500); // Wait for fire-and-forget activity log
            var recent = await activityLog.RecentAsync("test", limit: 10);
            await Assert.That(recent.Count).IsGreaterThanOrEqualTo(1);

            var rejection = recent.FirstOrDefault(e => e.Action == "admission_rejected");
            await Assert.That(rejection).IsNotNull();
            await Assert.That(rejection!.Action).IsEqualTo("admission_rejected");
            await Assert.That(rejection.Wing).IsEqualTo("test");
        }
        finally { File.Delete(db); }
    }

    private sealed class FakeEmbedderVec : IEmbedder
    {
        public int EmbeddingDimension => 512;
        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var vec = new float[512];
            for (var i = 0; i < 512; i++)
                vec[i] = 0.1f;
            return new ValueTask<float[]>(vec);
        }

        public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            var results = new float[texts.Count][];
            for (var i = 0; i < texts.Count; i++)
            {
                var vec = new float[512];
                for (var j = 0; j < 512; j++)
                    vec[j] = 0.1f;
                results[i] = vec;
            }
            return Task.FromResult(results);
        }
    }
}
