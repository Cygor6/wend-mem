using DuckDB.NET.Data;
using Wendmem.Models;
using Wendmem.Services;
using Wendmem.Storage;

namespace Wendmem.Tests;

/// <summary>
/// Geometry and regime tests using synthetic embeddings with known properties.
/// No LLM calls - deterministic vectors backed by a temp-file DuckDB.
/// </summary>
public class GeometryTests
{
    DuckDbConnectionFactory _factory = null!;
    string _dbPath = null!;
    DrawerStorage _storage = null!;

    [Before(Test)]
    public void Initialize()
    {
        _dbPath = Path.GetTempFileName() + ".duckdb";
        _factory = new DuckDbConnectionFactory(_dbPath);

        var embedder = new ZeroEmbedder();
        var closets = new ClosetStorage(_factory);
        var aaak = new AaakDialect();
        var entityIndex = new EntityIndexService(_factory);
        var kg = new KnowledgeGraph(_factory);
        _storage = new DrawerStorage(_factory, embedder, closets, aaak, entityIndex, kg, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 500 }),
        new PalaceConfig { AdmissionEnabled = false });
    }

    [After(Test)]
    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    sealed class ZeroEmbedder : IEmbedder
    {
        public int EmbeddingDimension => 512;
        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var v = new float[512];
            return new ValueTask<float[]>(v);
        }
    }

    /// <summary>Add a drawer with a pre-computed embedding via raw SQL.</summary>
    async Task<string> AddWithEmbedding(
        string content, string wing, float[] embedding,
        string room = "test", string source = "synthetic")
    {
        var id = ComputeTestHash(content);
        var embLiteral = FormatVector(embedding);
        await _factory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO drawers (id, wing, room, content, fts_text, content_hash,
                    source, source_mtime, importance, drawer_type, embedding, valid_from)
                VALUES ($id, $wing, $room, $content, $fts, $hash,
                    $source, NULL, 0.5, 'source', {embLiteral}, DEFAULT)
                ON CONFLICT (id) DO NOTHING
                """;
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
            cmd.Parameters.Add(new DuckDBParameter("room", room));
            cmd.Parameters.Add(new DuckDBParameter("content", content));
            cmd.Parameters.Add(new DuckDBParameter("fts", $"{content} {wing} {room}"));
            cmd.Parameters.Add(new DuckDBParameter("hash", content.GetHashCode().ToString("x")));
            cmd.Parameters.Add(new DuckDBParameter("source", source));
            cmd.ExecuteNonQuery();
        });
        return id;
    }

    static string ComputeTestHash(string content)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    static float[] MakeVector(int direction, float noise = 0f, uint seed = 0)
    {
        var v = new float[512];
        for (var j = 0; j < 512; j++)
            v[j] = MathF.Cos((direction * MathF.PI) / 6f + j * 0.001f)
                 + MathF.Sin(seed + j) * noise;
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0f)
            for (var j = 0; j < 512; j++)
                v[j] /= norm;
        return v;
    }

    static string FormatVector(float[] v) =>
        "[" + string.Join(",", v.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]::FLOAT[512]";

    // ══ Cosine Similarity ═══════════════════════════════════════

    [Test]
    public async Task CosineSimilarity_IdenticalVectors_IsOne()
    {
        var v = MakeVector(0);
        var sim = DrawerStorage.CosineSimilarity(v, v);
        await Assert.That(sim).IsEqualTo(1.0f).Within(0.001f);
    }

    [Test]
    public async Task CosineSimilarity_OppositeVectors_IsMinusOne()
    {
        var v1 = MakeVector(0);
        var v2 = v1.Select(x => -x).ToArray();
        var sim = DrawerStorage.CosineSimilarity(v1, v2);
        await Assert.That(sim).IsEqualTo(-1.0f).Within(0.001f);
    }

    // ══ Regime Logic ════════════════════════════════════════════

    [Test]
    public async Task ComputeRegime_TightCluster()
    {
        var regime = DrawerResult.ComputeRegime(0.01f, 16f, 0.85f);
        await Assert.That(regime).IsEqualTo(ClusterRegime.Tight);
    }

    [Test]
    public async Task ComputeRegime_SpreadCluster()
    {
        var regime = DrawerResult.ComputeRegime(0.20f, 16f, 0.85f);
        await Assert.That(regime).IsEqualTo(ClusterRegime.Spread);
    }

    [Test]
    public async Task ComputeRegime_UnknownWhenDBarZero()
    {
        var regime = DrawerResult.ComputeRegime(0f, 16f, 0.85f);
        await Assert.That(regime).IsEqualTo(ClusterRegime.Unknown);
    }

    [Test]
    public async Task ComputeRegime_BoundaryCase()
    {
        var regime = DrawerResult.ComputeRegime(0.15f, 16f, 0.85f);
        await Assert.That(regime).IsEqualTo(ClusterRegime.Spread);
    }

    // ══ Cluster Geometry via DB ═════════════════════════════════

    [Test]
    public async Task ClusterGeometry_Singleton_DBarZero()
    {
        var v = MakeVector(0);
        var id = await AddWithEmbedding("singleton", "geo-single", v);
        await _storage.RebuildFtsIndexAsync(default);

        var drawer = await _storage.GetByIdAsync(id, default);
        await Assert.That(drawer).IsNotNull();
        await Assert.That(drawer!.ClusterDBar).IsNotNull();
        await Assert.That(drawer.ClusterDBar!.Value).IsEqualTo(0f).Within(0.001f);
    }

    [Test]
    public async Task ClusterGeometry_IdenticalPair_DBarZero()
    {
        var v = MakeVector(0);
        var id1 = await AddWithEmbedding("Alpha content one", "geo-pair", v);
        var id2 = await AddWithEmbedding("Alpha content two", "geo-pair", v);
        await _storage.RebuildFtsIndexAsync(default);

        var d1 = await _storage.GetByIdAsync(id1, default);
        var d2 = await _storage.GetByIdAsync(id2, default);
        await Assert.That(d1).IsNotNull();
        await Assert.That(d2).IsNotNull();
        await Assert.That(d1!.ClusterDBar!.Value).IsEqualTo(0f).Within(0.01f);
        await Assert.That(d1.ClusterId).IsEqualTo(d2!.ClusterId);
    }

    [Test]
    public async Task ClusterGeometry_SimilarPair_LowDBar()
    {
        var v1 = MakeVector(0);
        var v2 = MakeVector(0, noise: 0.01f, seed: 42);
        var id1 = await AddWithEmbedding("Similar A", "geo-similar", v1);
        var id2 = await AddWithEmbedding("Similar B", "geo-similar", v2);
        await _storage.RebuildFtsIndexAsync(default);

        var d1 = await _storage.GetByIdAsync(id1, default);
        await Assert.That(d1).IsNotNull();
        await Assert.That(d1!.ClusterDBar!.Value).IsLessThanOrEqualTo(0.1f);
    }

    [Test]
    public async Task ClusterGeometry_DistantVectors_DifferentClusters()
    {
        var v1 = MakeVector(0);
        var v2 = MakeVector(3);
        var id1 = await AddWithEmbedding("Dir A far", "geo-far", v1);
        var id2 = await AddWithEmbedding("Dir D far", "geo-far", v2);
        await _storage.RebuildFtsIndexAsync(default);

        var d1 = await _storage.GetByIdAsync(id1, default);
        var d2 = await _storage.GetByIdAsync(id2, default);
        await Assert.That(d1).IsNotNull();
        await Assert.That(d2).IsNotNull();
        await Assert.That(d1!.ClusterDBar!.Value).IsEqualTo(0f).Within(0.001f);
        await Assert.That(d2!.ClusterDBar!.Value).IsEqualTo(0f).Within(0.001f);
        await Assert.That(d1.ClusterId).IsNotEqualTo(d2.ClusterId);
    }

    [Test]
    public async Task ClusterGeometry_ClusterWithSpread_HighDBar()
    {
        var v1 = MakeVector(0, noise: 0.3f, seed: 1);
        var v2 = MakeVector(0, noise: 0.3f, seed: 100);
        var v3 = MakeVector(0, noise: 0.3f, seed: 200);
        await AddWithEmbedding("Spread one", "geo-spread", v1);
        await AddWithEmbedding("Spread two", "geo-spread", v2);
        await AddWithEmbedding("Spread three", "geo-spread", v3);
        await _storage.RebuildFtsIndexAsync(default);

        await using var ro = _factory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT cluster_id, cluster_d_bar FROM drawers
            WHERE wing = $w AND cluster_id IS NOT NULL
            ORDER BY cluster_id
            """;
        cmd.Parameters.Add(new DuckDBParameter("w", "geo-spread"));
        using var reader = cmd.ExecuteReader();
        var ids = new HashSet<int>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
        }
        await Assert.That(ids.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Prune_TightCluster_KeepsMedoid()
    {
        var v = MakeVector(0);
        await AddWithEmbedding("Tight one", "prune-tight", v);
        await AddWithEmbedding("Tight two", "prune-tight", v);
        await AddWithEmbedding("Tight three", "prune-tight", v);

        var report = await _storage.PruneAsync("prune-tight", 0.97f, default);

        await Assert.That(report.Clusters).IsEqualTo(1);
        await Assert.That(report.Kept).IsEqualTo(1);
        await Assert.That(report.Retired).IsEqualTo(2);
    }

    [Test]
    public async Task Prune_SingleDrawer_NoRetirement()
    {
        var v = MakeVector(0);
        await AddWithEmbedding("Just one", "prune-single", v);

        var report = await _storage.PruneAsync("prune-single", 0.97f, default);

        await Assert.That(report.Retired).IsEqualTo(0);
        await Assert.That(report.Kept).IsEqualTo(1);
    }

    [Test]
    public async Task Prune_DistantSingletons_NothingRetired()
    {
        var v1 = MakeVector(0);
        var v2 = MakeVector(2);
        var v3 = MakeVector(4);
        await AddWithEmbedding("Far A", "prune-far", v1);
        await AddWithEmbedding("Far C", "prune-far", v2);
        await AddWithEmbedding("Far E", "prune-far", v3);

        var report = await _storage.PruneAsync("prune-far", 0.97f, default);

        await Assert.That(report.Retired).IsEqualTo(0);
        await Assert.That(report.Clusters).IsEqualTo(3);
    }

    [Test]
    public async Task Prune_SoftRetire_TotalCountUnchanged()
    {
        var v = MakeVector(0);
        await AddWithEmbedding("Count A", "prune-count", v);
        await AddWithEmbedding("Count B", "prune-count", v);

        long before;
        await using (var ro = _factory.OpenReadOnly())
        {
            using var cmd = ro.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM drawers WHERE wing = $w";
            cmd.Parameters.Add(new DuckDBParameter("w", "prune-count"));
            before = (long)cmd.ExecuteScalar()!;
        }

        await _storage.PruneAsync("prune-count", 0.97f, default);

        long after;
        await using (var ro2 = _factory.OpenReadOnly())
        {
            using var cmd2 = ro2.CreateCommand();
            cmd2.CommandText = "SELECT count(*) FROM drawers WHERE wing = $w";
            cmd2.Parameters.Add(new DuckDBParameter("w", "prune-count"));
            after = (long)cmd2.ExecuteScalar()!;
        }

        await Assert.That(after).IsEqualTo(before);
    }

    [Test]
    public async Task Prune_NonRetiredAreStillRepresentative()
    {
        var v = MakeVector(0);
        await AddWithEmbedding("Rep A", "prune-rep", v);
        await AddWithEmbedding("Rep B", "prune-rep", v);
        await AddWithEmbedding("Rep C", "prune-rep", v);

        await _storage.PruneAsync("prune-rep", 0.97f, default);

        await using var ro = _factory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT count(*) FROM drawers
            WHERE wing = $w AND is_representative = TRUE
            """;
        cmd.Parameters.Add(new DuckDBParameter("w", "prune-rep"));
        var repCount = (long)cmd.ExecuteScalar()!;
        await Assert.That(repCount).IsEqualTo(1);
    }

    [Test]
    public async Task Prune_RetiredExcludedFromRecentDrawers()
    {
        var v = MakeVector(0);
        await AddWithEmbedding("Recent A", "prune-recent", v);
        await AddWithEmbedding("Recent B", "prune-recent", v);
        await AddWithEmbedding("Recent C", "prune-recent", v);

        await _storage.PruneAsync("prune-recent", 0.97f, default);

        var recent = await _storage.RecentDrawersAsync("prune-recent", 10, default);
        await Assert.That(recent.Count).IsEqualTo(1);
        await Assert.That(recent[0].IsRepresentative).IsTrue();
    }

    // ══ IsRepresentative Filter ════════════════════════════════

    [Test]
    public async Task Search_SkipsSoftRetiredDrawers()
    {
        var id = (await _storage.AddDrawerAsync(
            content: "This should not appear in search results",
            wing: "filter-geo", room: "docs",
            source: "test.md", sourceMtime: null,
            drawerType: "source", ct: default))?.Id;

        await _factory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE drawers SET is_representative = FALSE WHERE id = $id";
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            cmd.ExecuteNonQuery();
        });

        await _storage.RebuildFtsIndexAsync(default);

        var results = await _storage.FtsSearchAsync(
            "should not appear", "filter-geo", 10, default);
        var found = results.Any(r => r.Drawer.Id == id);
        await Assert.That(found).IsFalse();
    }

    // ══ Empty Wing Tests ═══════════════════════════════════════

    [Test]
    public async Task Prune_EmptyWing_ReturnsZeroReport()
    {
        var report = await _storage.PruneAsync("empty-wing", 0.97f, default);
        await Assert.That(report.Clusters).IsEqualTo(0);
        await Assert.That(report.Retired).IsEqualTo(0);
        await Assert.That(report.Kept).IsEqualTo(0);
    }

    [Test]
    public async Task Search_EmptyWing_ReturnsEmpty()
    {
        var embedder = new ZeroEmbedder();
        var queryVec = await embedder.EmbedAsync("anything", default);
        var results = await _storage.SearchAsync(
            queryVec, "no-such-wing", null, 5, 0.85f, default);
        await Assert.That(results.Count).IsEqualTo(0);
    }
}
