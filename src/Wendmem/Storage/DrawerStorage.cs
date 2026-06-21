using System.Buffers;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DuckDB.NET.Data;
using Microsoft.Extensions.Caching.Memory;
using Wendmem.Models;
using Wendmem.Services;

namespace Wendmem.Storage;

sealed class DrawerStorage(DuckDbConnectionFactory dbFactory, IEmbedder embedder, ClosetStorage closets, AaakDialect aaak, EntityIndexService entityIndex, KnowledgeGraph kg, IMemoryCache cache, PalaceConfig palaceConfig, ActivityLog? activityLog = null, ImportanceScorer? importanceScorer = null)
{
    readonly List<(string Id, string Wing, string Room, string Content, string FtsText)> _delta = [];
    readonly Lock _deltaLock = new();
    readonly IMemoryCache _cache = cache;
    readonly PalaceConfig palaceConfig = palaceConfig;

    public async Task<AdmissionResult> AddDrawerAsync(
        string content, string wing, string room,
        string? source, long? sourceMtime,
        string drawerType = "source",
        CancellationToken ct = default)
    {
        var id = ComputeId(content);
        var contentHash = ComputeHash(content);
        var ftsText = BuildFtsText(content, wing, room);

        if (palaceConfig.AdmissionEnabled && drawerType == "source")
        {
            var adm = await ShouldAdmitAsync(content, null, wing, ct);
            if (!adm.Admitted)
            {
                ActivityLogFireAndForget("admission_rejected", wing, room,
                    $"{adm.Reason}: {content[..Math.Min(80, content.Length)]}");
                return new AdmissionResult(adm.MatchedId ?? adm.Id, false, adm.Reason, adm.MatchedId);
            }
        }
        // Compute heuristic importance score (zero deps, <5ms)
        float importanceScore = importanceScorer?.ScoreHeuristic(content) ?? 1.0f;

        var inserted = await dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
            INSERT INTO drawers
                (id, wing, room, content, fts_text, content_hash, source, source_mtime,
                 importance, drawer_type, valid_from)
            VALUES
                ($id, $wing, $room, $content, $fts_text, $content_hash, $source,
                 $source_mtime, $importance, $drawer_type, now())
            ON CONFLICT (id) DO NOTHING
            """;
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
            cmd.Parameters.Add(new DuckDBParameter("room", room));
            cmd.Parameters.Add(new DuckDBParameter("content", content));
            cmd.Parameters.Add(new DuckDBParameter("fts_text", ftsText));
            cmd.Parameters.Add(new DuckDBParameter("content_hash", contentHash));
            cmd.Parameters.Add(new DuckDBParameter("source", (object?)source ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("source_mtime", (object?)sourceMtime ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("drawer_type", drawerType));
            cmd.Parameters.Add(new DuckDBParameter("importance", (double)importanceScore));

            var rowsAffected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            if (rowsAffected == 1)
            {
                var vec = await embedder.EmbedDocumentAsync(content, ct);
                var literal = EmbeddingUtils.ToFloatArrayLiteral(vec);
                using var embedCmd = db.CreateCommand();
                embedCmd.CommandText = $"""
                UPDATE drawers
                SET embedding      = {literal}::FLOAT[{vec.Length}],
                    embedding_text = $embedding_text
                WHERE id = $id
                """;
                embedCmd.Parameters.Add(new DuckDBParameter("id", id));
                embedCmd.Parameters.Add(new DuckDBParameter("embedding_text", content));
                embedCmd.ExecuteNonQuery();
                return true;
            }
            return false;
        }, ct);

        if (inserted)
        {
            var meta = new AaakMetadata(
                SourceFile: source,
                Wing: wing,
                Room: room,
                Date: DateOnly.FromDateTime(DateTime.UtcNow));

            var aaakText = aaak.Compress(content, meta);
            await closets.AddClosetAsync(id, aaakText, wing, room, source, ct);

            await entityIndex.IndexDrawerAsync(id, content, ct);

            lock (_deltaLock)
            { _delta.Add((id, wing, room, content, ftsText)); }

            if (drawerType == "synthesis")
                _cache.Remove($"synthesis:{wing}");
        }

        return new AdmissionResult(id, true, null, null);
    }

    // DeleteDrawerAsync previously left deleted content searchable in the FTS index
    // because DuckDB FTS is a snapshot index — it does not update on row deletion.
    // This method invalidate the memory cache entry and trigger a full FTS rebuild after deletion.
    public async Task<bool> DeleteDrawerAsync(string id, CancellationToken ct)
    {
        var deleted = await dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM drawers WHERE id = $id";
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return affected > 0;
        }, ct);

        if (deleted)
        {
            _cache.Remove($"drawer:{id}");

            await RebuildFtsIndexAsync(ct);
        }

        return deleted;
    }

    public async Task<Drawer?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var key = $"drawer:{id}";
        if (_cache.TryGetValue(key, out Drawer? cached))
            return cached;

        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT id, wing, room, content, source, drawer_type, mined_at,
                   is_representative, cluster_id, cluster_d_bar, cluster_d_eff
            FROM drawers WHERE id = $id LIMIT 1
            """;
        cmd.Parameters.Add(new DuckDBParameter("id", id));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        var drawer = new Drawer(
            Id: reader.GetString(0),
            Wing: reader.GetString(1),
            Room: reader.GetString(2),
            Content: reader.GetString(3),
            FtsText: null,
            EmbeddingText: null,
            ParentId: null,
            ContentHash: string.Empty,
            Source: reader.IsDBNull(4) ? null : reader.GetString(4),
            SourceMtime: null,
            Importance: 1f,
            DrawerType: reader.IsDBNull(5) ? "source" : reader.GetString(5),
            MinedAt: new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero),
            ValidFrom: DateTimeOffset.UtcNow,
            ValidTo: null,
            IsRepresentative: reader.IsDBNull(7) || reader.GetBoolean(7),
            ClusterId: reader.IsDBNull(8) ? null : reader.GetInt32(8),
            ClusterDBar: reader.IsDBNull(9) ? null : reader.GetFloat(9),
            ClusterDEff: reader.IsDBNull(10) ? null : reader.GetFloat(10),
            Embedding: null);
        _cache.Set(key, drawer, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(30),
            Size = 1
        });
        RecordAccess([drawer.Id]);
        return drawer;
    }

    public async Task UpsertDrawerAsync(
        string id, string wing, string room, string content,
        string? source, long? sourceMtime, string drawerType,
        CancellationToken ct)
    {
        await dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO drawers (id, wing, room, content, fts_text, content_hash, source, source_mtime,
                                     importance, drawer_type, valid_from)
                VALUES ($id, $wing, $room, $content, $fts_text, $content_hash, $source, $source_mtime,
                        1.0, $drawer_type, now())
                ON CONFLICT (id) DO UPDATE SET
                    content     = excluded.content,
                    fts_text    = excluded.fts_text,
                    mined_at    = now(),
                    drawer_type = excluded.drawer_type
                """;
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
            cmd.Parameters.Add(new DuckDBParameter("room", room));
            cmd.Parameters.Add(new DuckDBParameter("content", content));
            cmd.Parameters.Add(new DuckDBParameter("fts_text", BuildFtsText(content, wing, room)));
            cmd.Parameters.Add(new DuckDBParameter("content_hash", ComputeHash(content)));
            cmd.Parameters.Add(new DuckDBParameter("source", (object?)source ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("source_mtime", (object?)sourceMtime ?? DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("drawer_type", drawerType));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            var vec = await embedder.EmbedDocumentAsync(content, ct);
            var literal = EmbeddingUtils.ToFloatArrayLiteral(vec);
            using var embedCmd = db.CreateCommand();
            embedCmd.CommandText = $"""
                UPDATE drawers
                SET embedding      = {literal}::FLOAT[{vec.Length}],
                    embedding_text = $embedding_text
                WHERE id = $id
                """;
            embedCmd.Parameters.Add(new DuckDBParameter("id", id));
            embedCmd.Parameters.Add(new DuckDBParameter("embedding_text", content));
            embedCmd.ExecuteNonQuery();
        }, ct);

        await entityIndex.IndexDrawerAsync(id, content, ct);

        if (drawerType == "synthesis")
            _cache.Remove($"synthesis:{wing}");
    }

    public async Task<IReadOnlyList<Drawer>> SynthesisDrawersAsync(string? wing, CancellationToken ct)
    {
        var cacheKey = $"synthesis:{wing ?? ""}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<Drawer>? cached))
            return cached!;

        var wingFilter = wing is not null ? "AND wing = $wing" : "";
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, wing, room, content, source, drawer_type, mined_at
            FROM drawers
            WHERE drawer_type = 'synthesis'
              AND is_representative
              AND valid_to IS NULL
              {wingFilter}
            ORDER BY mined_at DESC
            """;
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));

        var list = new List<Drawer>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new Drawer(
                reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3),
                FtsText: null, EmbeddingText: null, ParentId: null,
                ContentHash: string.Empty,
                Source: reader.IsDBNull(4) ? null : reader.GetString(4),
                SourceMtime: null, Importance: 1f,
                DrawerType: reader.IsDBNull(5) ? "source" : reader.GetString(5),
                MinedAt: new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero),
                ValidFrom: DateTimeOffset.UtcNow, ValidTo: null
            ));
        }

        _cache.Set(cacheKey, (IReadOnlyList<Drawer>)list, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(5),
            Size = 1
        });
        return list;
    }

    public async Task<IReadOnlyList<(string Wing, string Room)>> ListWingsRoomsAsync(CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT wing, room
            FROM drawers
            WHERE valid_to IS NULL
              AND is_representative
            ORDER BY wing, room
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<(string Wing, string Room)>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add((reader.GetString(0), reader.GetString(1)));
        return results;
    }

    public async Task<long> CountAsync(CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM drawers WHERE valid_to IS NULL AND is_representative";
        var count = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        return count;
    }

    public async Task<long?> GetSourceMtimeAsync(string sourcePath, CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT source_mtime FROM drawers
            WHERE source = $source AND valid_to IS NULL
            LIMIT 1
            """;
        cmd.Parameters.Add(new DuckDBParameter("source", sourcePath));
        var result = await Task.Run(() => cmd.ExecuteScalar(), ct);
        return result is DBNull or null ? null : Convert.ToInt64(result);
    }

    public async Task RebuildFtsIndexAsync(CancellationToken ct)
    {
        await dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                PRAGMA create_fts_index('drawers', 'id', 'fts_text', stemmer = 'none', stopwords = 'none', "ignore" = '([^a-zA-Z0-9_åäöÅÄÖ])+', overwrite=1);
                PRAGMA create_fts_index('closets', 'id', 'aaak_text', stemmer = 'none', stopwords = 'none', "ignore" = '([^a-zA-Z0-9_åäöÅÄÖ])+', overwrite=1);
                PRAGMA create_fts_index('wiki_pages', 'path', 'content', 'title', stemmer = 'none', stopwords = 'none', "ignore" = '([^a-zA-Z0-9_åäöÅÄÖ])+', overwrite=1)
                """;
            cmd.ExecuteNonQuery();
        }, ct);

        await ComputeAndStoreClusterGeometryAsync(ct);

        await dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "CHECKPOINT";
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        lock (_deltaLock)
        { _delta.Clear(); }
    }

    // Runs k-means style clustering per wing. Uses brute-force pairwise within each
    // wing — fine at typical palace sizes (<50K drawers per wing).
    private async Task ComputeAndStoreClusterGeometryAsync(CancellationToken ct)
    {
        var wings = await ListWingsRoomsAsync(ct);
        var wingNames = wings.Select(w => w.Wing).Distinct().ToList();
        int nextClusterId = 0;

        // Compute all cluster data outside the write lock
        var allUpdates = new List<(string Id, int ClusterId, float DBar, float DEff)>();

        foreach (var wing in wingNames)
        {
            ct.ThrowIfCancellationRequested();
            var drawers = await LoadEmbeddingsForWingAsync(wing, ct);
            if (drawers.Count == 0)
                continue;

            const float GeometryThreshold = 0.90f;
            var clusters = BuildClusters(drawers, GeometryThreshold, startClusterId: nextClusterId);
            nextClusterId += clusters.Count;

            foreach (var (cid, members) in clusters)
            {
                var (dBar, dEff) = ClusterGeometry(members);
                foreach (var d in members)
                    allUpdates.Add((d.Id, cid, dBar, dEff));
            }
        }

        // Write all cluster data under a single lock acquisition
        await dbFactory.ExecuteWriteAsync(db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE drawers
                SET cluster_id    = $cluster_id,
                    cluster_d_bar = $d_bar,
                    cluster_d_eff = $d_eff
                WHERE id = $id
                """;
            var pId = new DuckDBParameter("id", "");
            var pCluster = new DuckDBParameter("cluster_id", 0);
            var pDBar = new DuckDBParameter("d_bar", 0f);
            var pDEff = new DuckDBParameter("d_eff", 0f);
            cmd.Parameters.Add(pId);
            cmd.Parameters.Add(pCluster);
            cmd.Parameters.Add(pDBar);
            cmd.Parameters.Add(pDEff);

            foreach (var (id, cid, dBar, dEff) in allUpdates)
            {
                pId.Value = id;
                pCluster.Value = cid;
                pDBar.Value = dBar;
                pDEff.Value = dEff;
                cmd.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }, ct);
    }

    private async Task<List<EmbeddingRow>> LoadEmbeddingsForWingAsync(string wing, CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT id, embedding
            FROM drawers
            WHERE wing = $wing
              AND valid_to IS NULL
              AND is_representative
              AND embedding IS NOT NULL
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));

        var rows = new List<EmbeddingRow>();
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (reader.Read())
        {
            var raw = reader.GetValue(1);
            float[] vec = raw switch
            {
                float[] f => f,
                List<float> lf => lf.ToArray(),
                IReadOnlyList<float> rlf => rlf.ToArray(),
                object[] o => Array.ConvertAll(o, x => Convert.ToSingle(x)),
                System.Collections.ICollection c => c.Cast<float>().ToArray(),
                _ => throw new InvalidCastException($"Unexpected embedding type: {raw.GetType()}")
            };
            rows.Add(new EmbeddingRow(reader.GetString(0), vec));
        }
        return rows;
    }

    private static List<(int ClusterId, List<EmbeddingRow> Members)> BuildClusters(
        List<EmbeddingRow> drawers, float threshold, int startClusterId = 0)
    {
        var parent = Enumerable.Range(0, drawers.Count).ToArray();
        int Find(int x)
        { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b)
        { parent[Find(a)] = Find(b); }

        for (int i = 0; i < drawers.Count; i++)
            for (int j = i + 1; j < drawers.Count; j++)
            {
                if (CosineSimilarity(drawers[i].Embedding, drawers[j].Embedding) >= threshold)
                    Union(i, j);
            }

        return drawers
            .Select((d, i) => (Root: Find(i), Index: i, Drawer: d))
            .GroupBy(x => x.Root)
            .Select((g, seq) => (startClusterId + seq, g.Select(x => x.Drawer).ToList()))
            .ToList();
    }

    // Participation-ratio effective dimension and mean pairwise cosine distance.
    private static (float DBar, float DEff) ClusterGeometry(List<EmbeddingRow> members)
    {
        if (members.Count == 1)
            return (0f, 1f);
        int d = members[0].Embedding.Length;
        int n = members.Count;

        float dBarSum = 0f;
        int pairs = 0;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                dBarSum += 1f - CosineSimilarity(members[i].Embedding, members[j].Embedding);
                pairs++;
            }
        float dBar = pairs > 0 ? dBarSum / pairs : 0f;

        // Participation-ratio d_eff = (Σλᵢ)² / Σλᵢ²
        // Compute eigenvalues via n×n Gram matrix (cheaper than d×d covariance for n < d).
        var pool = ArrayPool<float>.Shared;
        float[] mean = pool.Rent(d);
        try
        {
            var meanSpan = mean.AsSpan(0, d);
            meanSpan.Clear();
            foreach (var row in members)
                for (int k = 0; k < d; k++)
                    mean[k] += row.Embedding[k] / n;

            var centered = members.Select(m =>
                m.Embedding.Select((v, k) => v - mean[k]).ToArray()).ToList();

            var gram = new float[n, n];
            for (int i = 0; i < n; i++)
                for (int j = i; j < n; j++)
                {
                    float dot = TensorPrimitives.Dot(
                        centered[i].AsSpan(0, d), centered[j].AsSpan(0, d));
                    gram[i, j] = gram[j, i] = dot;
                }

            var eigenvalues = JacobiEigenvalues(gram, n);
            float sumL = eigenvalues.Sum();
            float sumL2 = eigenvalues.Sum(l => l * l);
            float dEff = sumL2 > 0f ? (sumL * sumL) / sumL2 : 1f;
            return (dBar, dEff);
        }
        finally
        {
            pool.Return(mean);
        }
    }

    // Eigenvalues of symmetric matrix via Jacobi rotations — good enough for n ≤ 200.
    private static float[] JacobiEigenvalues(float[,] A, int n)
    {
        var a = new float[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                a[i, j] = A[i, j];

        const int maxIter = 100;
        for (int iter = 0; iter < maxIter; iter++)
        {
            float maxVal = 0f;
            int p = 0, q = 1;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (MathF.Abs(a[i, j]) > maxVal)
                    { maxVal = MathF.Abs(a[i, j]); p = i; q = j; }
                }
            if (maxVal < 1e-10f)
                break;

            float app = a[p, p], aqq = a[q, q], apq = a[p, q];
            float theta = (aqq - app) / (2f * apq);
            float t = MathF.Sign(theta) / (MathF.Abs(theta) + MathF.Sqrt(1f + theta * theta));
            float c = 1f / MathF.Sqrt(1f + t * t);
            float s = t * c;

            for (int i = 0; i < n; i++)
            {
                if (i == p || i == q)
                    continue;
                float aip = a[i, p], aiq = a[i, q];
                a[i, p] = a[p, i] = c * aip - s * aiq;
                a[i, q] = a[q, i] = s * aip + c * aiq;
            }
            a[p, p] = c * c * app - 2f * s * c * apq + s * s * aqq;
            a[q, q] = s * s * app + 2f * s * c * apq + c * c * aqq;
            a[p, q] = a[q, p] = 0f;
        }

        var eigenvalues = new float[n];
        for (int i = 0; i < n; i++)
            eigenvalues[i] = MathF.Max(0f, a[i, i]);
        return eigenvalues;
    }

    private static EmbeddingRow FindMedoid(List<EmbeddingRow> members)
    {
        int d = members[0].Embedding.Length;
        var pool = ArrayPool<float>.Shared;
        float[] centroid = pool.Rent(d);
        try
        {
            var span = centroid.AsSpan(0, d);
            span.Clear();
            foreach (var m in members)
                for (int k = 0; k < d; k++)
                    centroid[k] += m.Embedding[k] / members.Count;
            float norm = TensorPrimitives.Norm(span);
            if (norm > 0f)
                TensorPrimitives.Divide(span, norm, span);
            return members.MaxBy(m => CosineSimilarity(m.Embedding, centroid))!;
        }
        finally
        {
            pool.Return(centroid);
        }
    }

    private static List<EmbeddingRow> GreedyMedoidSelection(List<EmbeddingRow> members, int m)
    {
        var reps = new List<EmbeddingRow> { FindMedoid(members) };
        while (reps.Count < m)
        {
            var next = members
                .Except(reps)
                .MaxBy(x => reps.Min(r =>
                    1f - CosineSimilarity(x.Embedding, r.Embedding)));
            if (next is null)
                break;
            reps.Add(next);
        }
        return reps;
    }

    public async Task<IReadOnlyList<DrawerResult>> SearchAsync(
        float[] queryVec, string? wing, string? room, int k,
        float theta = 0.85f, CancellationToken ct = default)
    {
        var vec = EmbeddingUtils.ToDuckDbFloatArray(queryVec);
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, wing, room, content, source, mined_at, embedding,
                   cluster_d_bar, cluster_d_eff,
                   array_cosine_similarity(embedding, {vec}) AS score,
                   access_count, last_accessed_at
            FROM drawers
            WHERE embedding IS NOT NULL
              AND is_representative = TRUE
              {(wing is not null ? "AND wing = $wing" : "")}
              {(room is not null ? "AND room = $room" : "")}
            ORDER BY score DESC
            LIMIT $fetch
            """;
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        if (room is not null)
            cmd.Parameters.Add(new DuckDBParameter("room", room));
        cmd.Parameters.Add(new DuckDBParameter("fetch", k));

        var results = new List<DrawerResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var raw = reader.GetValue(6);
            float[]? emb = raw is DBNull ? null : raw switch
            {
                float[] f => f,
                List<float> lf => lf.ToArray(),
                IReadOnlyList<float> rlf => rlf.ToArray(),
                object[] o => Array.ConvertAll(o, x => Convert.ToSingle(x)),
                System.Collections.ICollection c => c.Cast<float>().ToArray(),
                _ => null
            };
            float dBar = reader.IsDBNull(7) ? 0f : reader.GetFloat(7);
            float dEff = reader.IsDBNull(8) ? 0f : reader.GetFloat(8);
            var scoreRaw = reader.GetValue(9);
            float score = scoreRaw is float sf ? sf : Convert.ToSingle(scoreRaw);
            int accessCount = reader.IsDBNull(10) ? 0 : reader.GetInt32(10);
            DateTimeOffset? lastAccessedAt = reader.IsDBNull(11) ? null
                : new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero);
            var drawer = ReadDrawer(reader, emb, accessCount, lastAccessedAt);
            var regime = DrawerResult.ComputeRegime(dBar, dEff, theta);
            results.Add(new DrawerResult(drawer, score, regime));
        }
        RecordAccess(results.Select(r => r.Drawer.Id).ToList());
        return results.Select(ApplyDecayBoost).OrderByDescending(r => r.Score).ToList();
    }

    internal static IReadOnlyList<DrawerResult> MmrRerank(
        IReadOnlyList<DrawerResult> candidates, int k, float lambda)
    {
        if (candidates.Count <= k)
            return candidates;

        // Pre-extract embeddings; skip candidates without them (they contribute
        // zero similarity and would never win diversity scoring).
        var embArray = new float[candidates.Count][];
        for (int i = 0; i < candidates.Count; i++)
            embArray[i] = candidates[i].Drawer.Embedding;

        var available = new bool[candidates.Count];
        Array.Fill(available, true);

        var selectedIdx = new List<int>(k);
        float bestScore = float.MinValue;
        int bestIdx = -1;

        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Score > bestScore)
            { bestScore = candidates[i].Score; bestIdx = i; }
        }
        if (bestIdx < 0)
            return [];
        selectedIdx.Add(bestIdx);
        available[bestIdx] = false;

        float oneMinusLambda = 1f - lambda;

        while (selectedIdx.Count < k)
        {
            bestScore = float.MinValue;
            bestIdx = -1;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                if (!available[ci])
                    continue;

                var candEmb = embArray[ci];
                float relevance = lambda * candidates[ci].Score;

                float maxSim = 0f;
                if (candEmb is not null)
                {
                    for (int si = 0; si < selectedIdx.Count; si++)
                    {
                        var selEmb = embArray[selectedIdx[si]];
                        if (selEmb is null)
                            continue;
                        float sim = CosineSimilarity(candEmb, selEmb);
                        if (sim > maxSim)
                            maxSim = sim;
                    }
                }

                float mmr = relevance - oneMinusLambda * maxSim;
                if (mmr > bestScore)
                { bestScore = mmr; bestIdx = ci; }
            }

            if (bestIdx < 0)
                break;
            selectedIdx.Add(bestIdx);
            available[bestIdx] = false;
        }

        return selectedIdx.Select(i => candidates[i]).ToList();
    }

    private static Drawer ReadDrawer(System.Data.IDataRecord reader, float[]? embedding = null,
        int accessCount = 0, DateTimeOffset? lastAccessedAt = null)
    {
        return new Drawer(
            Id: reader.GetString(0),
            Wing: reader.GetString(1),
            Room: reader.GetString(2),
            Content: reader.GetString(3),
            FtsText: null, EmbeddingText: null, ParentId: null,
            ContentHash: string.Empty,
            Source: reader.IsDBNull(4) ? null : reader.GetString(4),
            SourceMtime: null,
            Importance: 1f,
            DrawerType: "source",
            MinedAt: new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero),
            ValidFrom: DateTimeOffset.UtcNow,
            ValidTo: null,
            Embedding: embedding,
            AccessCount: accessCount,
            LastAccessedAt: lastAccessedAt
        );
    }

    public async Task<IReadOnlyList<DrawerResult>> FtsSearchAsync(
        string query, string? wing, int limit, CancellationToken ct)
    {
        List<DrawerResult> bm25Results = [];
        try
        {
            var wingFilter = wing is not null ? "AND wing = $wing" : "";
            using var ro = dbFactory.OpenReadOnly();
            using var cmd = ro.CreateCommand();
            cmd.CommandText = $"""
                WITH scored AS (
                    SELECT id,
                           fts_main_drawers.match_bm25(id, $query, fields := 'fts_text') AS score
                    FROM drawers
                    WHERE valid_to IS NULL
                      AND is_representative
                      {wingFilter}
                )
                SELECT d.id, d.wing, d.room, d.content, d.source, s.score
                FROM scored s
                JOIN drawers d ON d.id = s.id
                WHERE s.score IS NOT NULL
                ORDER BY s.score DESC
                LIMIT $limit
                """;
            cmd.Parameters.Add(new DuckDBParameter("query", query));
            cmd.Parameters.Add(new DuckDBParameter("limit", limit));
            if (wing is not null)
                cmd.Parameters.Add(new DuckDBParameter("wing", wing));
            bm25Results = await ReadResultsAsync(cmd, ct);
        }
        catch (DuckDBException) { }

        var deltaResults = SearchDelta(query, wing);
        var seen = bm25Results.Select(r => r.Drawer.Id).ToHashSet();
        var merged = bm25Results.ToList();
        merged.AddRange(deltaResults.Where(r => !seen.Contains(r.Drawer.Id)));
        return merged.Take(limit).ToList();
    }

    public async Task<IReadOnlyList<DrawerResult>> CosinSearchAsync(
        float[] queryVec, string? wing, int limit, CancellationToken ct)
    {
        var literal = EmbeddingUtils.ToFloatArrayLiteral(queryVec);
        var wingFilter = wing is not null ? "AND wing = $wing" : "";
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, wing, room, content, source,
                   array_cosine_similarity(embedding, {literal}::FLOAT[{queryVec.Length}]) AS score
            FROM drawers
            WHERE embedding IS NOT NULL
              AND valid_to IS NULL
              AND is_representative
              {wingFilter}
            ORDER BY score DESC
            LIMIT $limit
            """;
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        return await ReadResultsAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<DrawerResult>> HybridSearchAsync(
        string query, float[] queryVec, string? wing, string? room, int k, CancellationToken ct,
        float mmrLambda = 0.5f,
        bool includeKgChannel = true)
    {
        var bm25DrawerTask = FtsSearchAsync(query, wing, limit: 200, ct);
        var bm25ClosetTask = closets.FtsSearchAsync(query, wing, limit: 200, ct);
        var cosineTask = CosinSearchAsync(queryVec, wing, limit: 200, ct);
        await Task.WhenAll(bm25DrawerTask, bm25ClosetTask, cosineTask);

        var drawerIds = bm25DrawerTask.Result.Select(r => r.Drawer.Id).ToList();
        var closetIds = bm25ClosetTask.Result.Select(r => r.DrawerId).ToList();
        var cosineIds = cosineTask.Result.Select(r => r.Drawer.Id).ToList();

        var kgQueryTokens = ExtractQueryTokens(query);
        var kgHits = await KgDrawerIdsAsync(kgQueryTokens, room, ct);

        // RRF fuse the three core channels (BM25 drawers, closets, cosine)
        var fusedIds = RrfFuse([drawerIds, closetIds, cosineIds]).ToList();

        var existingIds = new HashSet<string>(fusedIds);
        foreach (var (id, _) in kgHits)
        {
            if (!existingIds.Contains(id))
            {
                fusedIds.Add(id);
                existingIds.Add(id);
            }
        }

        if (fusedIds.Count == 0)
            return [];

        // Fetch drawer data for all fused IDs (including importance for salience scoring)
        var roomFilter = room is not null ? "AND room = $room" : "";
        var inList = string.Join(",", fusedIds.Select(id => $"'{id}'"));
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, wing, room, content, source, 0.0 AS score, access_count, last_accessed_at, importance
            FROM drawers
            WHERE id IN ({inList})
              AND valid_to IS NULL
              AND is_representative
              {roomFilter}
            """;
        if (room is not null)
            cmd.Parameters.Add(new DuckDBParameter("room", room));
        var rows = await ReadResultsWithImportanceAsync(cmd, ct);
        var byId = rows.ToDictionary(r => r.Drawer.Id);

        for (int kgRank = 0; kgRank < kgHits.Count; kgRank++)
        {
            var (id, confidence) = kgHits[kgRank];
            if (!byId.TryGetValue(id, out var existing))
                continue;
            var bonus = confidence / (60 + kgRank + 1);
            byId[id] = existing with { Score = existing.Score + bonus };
        }

        // Normalize scores to [0,1] so decay/boost and MMR operate on a consistent scale.
        var combined = fusedIds
            .Where(byId.ContainsKey)
            .Select(id => ApplyDecayBoost(byId[id]))
            .ToList();

        combined = MinMaxNormalize(combined);

        // Salience boost: add importance * weight to each score after normalization
        var salienceWeight = palaceConfig.SalienceWeight;
        if (salienceWeight > 0f)
            combined = ApplySalienceBoost(combined, salienceWeight);

        combined = combined.OrderByDescending(r => r.Score).ToList();
        var ranked = MmrRerank(combined, k, mmrLambda);
        RecordAccess(ranked.Select(r => r.Drawer.Id).ToList());
        return ranked;
    }

    /// <summary>
    /// Min-max normalize scores to [0,1] across the candidate set.
    /// </summary>
    private static List<DrawerResult> MinMaxNormalize(List<DrawerResult> results)
    {
        if (results.Count <= 1)
            return results;

        float min = float.MaxValue, max = float.MinValue;
        foreach (var r in results)
        {
            if (r.Score < min)
                min = r.Score;
            if (r.Score > max)
                max = r.Score;
        }

        float range = max - min;
        if (range < 1e-8f)
            return results;

        float invRange = 1f / range;
        for (int i = 0; i < results.Count; i++)
            results[i] = results[i] with { Score = (results[i].Score - min) * invRange };

        return results;
    }

    private static DrawerResult ApplyDecayBoost(DrawerResult r)
    {
        var daysSinceAccess = r.Drawer.LastAccessedAt.HasValue
            ? (float)(DateTimeOffset.UtcNow - r.Drawer.LastAccessedAt.Value).TotalDays
            : 0f;
        var decay = 1f / (1f + 0.002f * daysSinceAccess);
        var boost = 1f + 0.1f * MathF.Log10(1 + r.Drawer.AccessCount);
        return r with { Score = r.Score * decay * boost };
    }

    private static readonly HashSet<string> StopWords =
    [
        // English
        "a","an","the","is","are","was","were","be","been","being",
        "in","on","at","to","for","of","with","by","from","as","into",
        "and","or","but","not","no","if","then","than","so","this","that",
        "it","its","has","have","had","do","does","did","will","would",
        "can","could","should","may","might","must","shall",
        // Swedish
        "och","att","den","det","som","har","för","med","inte","kan","ska",
        "men","han","hon","från","eller","vid","mot","här","där","när","hur",
        "vad","vem","detta","denna","dessa","sin","sitt","sina","deras",
        "vår","vårt","våra","era","ert","genom","mellan","utan","samt","även",
        "bara","redan","alla","något","någon","några","ingen","inget","inga",
        "vara","blir","blev","sedan","efter","innan","under","över",
    ];

    /// <summary>
    /// Extract candidate tokens from a query string: split on whitespace/punctuation,
    /// lowercase, discard tokens shorter than 3 characters and common stop words.
    /// </summary>
    internal static List<string> ExtractQueryTokens(string query)
    {
        var tokens = new List<string>();
        var span = query.AsSpan();
        int start = 0;
        for (int i = 0; i <= span.Length; i++)
        {
            if (i == span.Length || char.IsWhiteSpace(span[i]) || char.IsPunctuation(span[i]))
            {
                if (i - start >= 3)
                {
                    var token = span[start..i].ToString().ToLowerInvariant();
                    if (!StopWords.Contains(token))
                        tokens.Add(token);
                }
                start = i + 1;
            }
        }
        return tokens;
    }

    /// <summary>
    /// Given query tokens, find drawers whose content mentions those tokens.
    /// Returns (drawerId, maxConfidence) pairs for RRF fusion weighted by confidence.
    /// </summary>
    private async Task<List<(string DrawerId, float Confidence)>> KgDrawerIdsAsync(
        IReadOnlyList<string> tokens, string? room, CancellationToken ct)
    {
        if (tokens.Count == 0)
            return [];

        var best = new Dictionary<string, float>();

        foreach (var token in tokens)
        {
            using var ro = dbFactory.OpenReadOnly();
            using var cmd = ro.CreateCommand();
            cmd.CommandText = """
                WITH direct AS (
                    SELECT DISTINCT t1.drawer_id, t1.object AS neighbor_id, t1.confidence
                    FROM entities e
                    JOIN triples t1 ON e.id = t1.subject
                    WHERE strip_accents(lower(e.name)) LIKE '%' || strip_accents(lower($token)) || '%'
                      AND t1.valid_to IS NULL
                      AND t1.drawer_id IS NOT NULL
                ),
                two_hop AS (
                    SELECT DISTINCT t2.drawer_id, t2.confidence
                    FROM direct d
                    JOIN triples t2 ON d.neighbor_id = t2.subject
                    WHERE t2.valid_to IS NULL
                      AND t2.drawer_id IS NOT NULL
                )
                SELECT drawer_id, confidence FROM direct
                UNION ALL
                SELECT drawer_id, confidence FROM two_hop
                """;
            cmd.Parameters.Add(new DuckDBParameter("token", token));

            using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var conf = reader.IsDBNull(1) ? 1f : Convert.ToSingle(reader.GetValue(1));
                ref float existing = ref CollectionsMarshal.GetValueRefOrAddDefault(best, id, out _);
                if (conf > existing)
                    existing = conf;
            }
        }

        return best.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public async Task<IReadOnlyList<Drawer>> RecentDrawersAsync(
        string? wing, int limit, CancellationToken ct)
    {
        var wingFilter = wing is not null ? "AND wing = $wing" : "";
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, wing, room, content, source
            FROM drawers
            WHERE valid_to IS NULL
              AND is_representative
              {wingFilter}
            ORDER BY COALESCE(last_accessed_at, mined_at) DESC
            LIMIT $limit
            """;
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));

        var list = new List<Drawer>();
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (reader.Read())
        {
            list.Add(new Drawer(
                Id: reader.GetString(0), Wing: reader.GetString(1),
                Room: reader.GetString(2), Content: reader.GetString(3),
                Source: reader.IsDBNull(4) ? null : reader.GetString(4),
                FtsText: null, EmbeddingText: null, ParentId: null,
                ContentHash: string.Empty, SourceMtime: null,
                Importance: 1f, DrawerType: "source",
                MinedAt: DateTimeOffset.UtcNow,
                ValidFrom: DateTimeOffset.UtcNow, ValidTo: null
            ));
        }
        return list;
    }

    public async Task<IReadOnlyList<Drawer>> GetRecentAttemptsAsync(
        string wing, int limit, CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT id, wing, room, content, source, mined_at
            FROM drawers
            WHERE wing     = $wing
              AND room     = 'attempts'
              AND is_representative = TRUE
              AND valid_to IS NULL
            ORDER BY mined_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));

        var results = new List<Drawer>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new Drawer(
                Id: reader.GetString(0),
                Wing: reader.GetString(1),
                Room: reader.GetString(2),
                Content: reader.GetString(3),
                Source: reader.IsDBNull(4) ? null : reader.GetString(4),
                FtsText: null, EmbeddingText: null, ParentId: null,
                ContentHash: string.Empty, SourceMtime: null,
                Importance: 1f, DrawerType: "source",
                MinedAt: new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero),
                ValidFrom: DateTimeOffset.UtcNow, ValidTo: null
            ));
        }
        return results;
    }

    public async Task<IReadOnlyList<Drawer>> RecentSourceDrawersAsync(
        string? wing, int limit, CancellationToken ct)
    {
        var wingFilter = wing is not null ? "AND wing = $wing" : "";
        using var ro2 = dbFactory.OpenReadOnly();
        using var cmd = ro2.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, wing, room, content, source
            FROM drawers
            WHERE valid_to IS NULL
              AND drawer_type = 'source'
              AND is_representative
              {wingFilter}
            ORDER BY COALESCE(last_accessed_at, mined_at) DESC
            LIMIT $limit
            """;
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));

        var list = new List<Drawer>();
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (reader.Read())
        {
            list.Add(new Drawer(
                Id: reader.GetString(0), Wing: reader.GetString(1),
                Room: reader.GetString(2), Content: reader.GetString(3),
                Source: reader.IsDBNull(4) ? null : reader.GetString(4),
                FtsText: null, EmbeddingText: null, ParentId: null,
                ContentHash: string.Empty, SourceMtime: null,
                Importance: 1f, DrawerType: "source",
                MinedAt: DateTimeOffset.UtcNow,
                ValidFrom: DateTimeOffset.UtcNow, ValidTo: null
            ));
        }
        return list;
    }

    public async Task<IReadOnlyList<DrawerResult>> CosinSearchSourceAsync(
        float[] queryVec, string? wing, int limit, HashSet<string>? excludeIds, CancellationToken ct)
    {
        var literal = EmbeddingUtils.ToFloatArrayLiteral(queryVec);
        var wingFilter = wing is not null ? "AND wing = $wing" : "";
        var excludeFilter = excludeIds is { Count: > 0 }
            ? $"AND id NOT IN ({string.Join(",", excludeIds.Select(id => $"'{id}'"))})"
            : "";
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, wing, room, content, source,
                   array_cosine_similarity(embedding, {literal}::FLOAT[{queryVec.Length}]) AS score
            FROM drawers
            WHERE embedding IS NOT NULL
              AND valid_to IS NULL
              AND drawer_type = 'source'
              AND is_representative
              {wingFilter}
              {excludeFilter}
            ORDER BY score DESC
            LIMIT $limit
            """;
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        return await ReadResultsAsync(cmd, ct);
    }

    public Task<string?> GetClosetAaakAsync(string drawerId, CancellationToken ct)
        => closets.GetAaakTextAsync(drawerId, ct);

    public async Task<IReadOnlyList<GrepExactResult>> GrepExactAsync(
        string pattern, string? wing, string? room, int k, CancellationToken ct)
    {
        using var cmd = dbFactory.OpenReadOnly().CreateCommand();

        // DuckDB regexp_matches returns true/false for full-string match.
        // Use regexp_extract for finding the matched portion for display.
        cmd.CommandText = """
            SELECT
                id,
                wing,
                room,
                content as text_content,
                source as source_file,
                mined_at
            FROM drawers
            WHERE regexp_matches(content, $pattern)
              AND ($wing IS NULL OR wing = $wing)
              AND ($room IS NULL OR room = $room)
              AND is_representative = TRUE
              AND valid_to IS NULL
            ORDER BY mined_at DESC
            LIMIT $k
            """;

        cmd.Parameters.Add(new DuckDBParameter("pattern", pattern));
        cmd.Parameters.Add(new DuckDBParameter("wing", (object?)wing ?? DBNull.Value));
        cmd.Parameters.Add(new DuckDBParameter("room", (object?)room ?? DBNull.Value));
        cmd.Parameters.Add(new DuckDBParameter("k", k));

        var results = new List<GrepExactResult>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var content = reader.GetString(3);
            var sourceFile = reader.IsDBNull(4) ? null : reader.GetString(4);

            results.Add(new GrepExactResult(
                Id: reader.GetString(0),
                Wing: reader.GetString(1),
                Room: reader.GetString(2),
                Content: content,
                SourceFile: sourceFile,
                MinedAt: reader.GetDateTime(5),
                Snippet: ExtractSnippet(content, pattern)
            ));
        }
        return results;
    }

    private static string ExtractSnippet(string content, string pattern)
    {
        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(content, pattern);
            if (!match.Success)
                return content[..Math.Min(200, content.Length)];
            int start = Math.Max(0, match.Index - 80);
            int end = Math.Min(content.Length, match.Index + match.Length + 120);
            var snippet = content[start..end];
            return start > 0 ? "…" + snippet : snippet;
        }
        catch
        {
            return content[..Math.Min(200, content.Length)];
        }
    }

    public async Task<IReadOnlyList<Drawer>> GrepAsync(
        string query, float[] queryVec,
        string? wing, string? room,
        int contextWindow,
        CancellationToken ct)
    {
        var hits = await HybridSearchAsync(query, queryVec, wing, room, k: 1, ct);
        if (hits.Count == 0)
            return [];
        var anchor = hits[0].Drawer;

        using var ro = dbFactory.OpenReadOnly();
        using var tsCmd = ro.CreateCommand();
        tsCmd.CommandText = "SELECT mined_at FROM drawers WHERE id = $id";
        tsCmd.Parameters.Add(new DuckDBParameter("id", anchor.Id));
        var minedAt = await Task.Run(() => tsCmd.ExecuteScalar(), ct);
        if (minedAt is null or DBNull)
            return [anchor];

        using var cmd = ro.CreateCommand();
        var wingFilter = wing is not null ? "AND wing = $wing" : "";
        var roomFilter = room is not null ? "AND room = $room" : "";

        cmd.CommandText = $"""
            (
                SELECT id, wing, room, content, source, mined_at
                FROM drawers
                WHERE valid_to IS NULL
                  AND is_representative
                  {wingFilter} {roomFilter}
                  AND mined_at <= $anchor_ts
                  AND id != $anchor_id
                ORDER BY mined_at DESC
                LIMIT $n
            )
            UNION ALL
            (
                SELECT id, wing, room, content, source, mined_at
                FROM drawers
                WHERE id = $anchor_id
                  AND valid_to IS NULL
            )
            UNION ALL
            (
                SELECT id, wing, room, content, source, mined_at
                FROM drawers
                WHERE valid_to IS NULL
                  AND is_representative
                  {wingFilter} {roomFilter}
                  AND mined_at > $anchor_ts
                ORDER BY mined_at ASC
                LIMIT $n
            )
            ORDER BY mined_at ASC
            """;

        cmd.Parameters.Add(new DuckDBParameter("anchor_ts", minedAt));
        cmd.Parameters.Add(new DuckDBParameter("anchor_id", anchor.Id));
        cmd.Parameters.Add(new DuckDBParameter("n", contextWindow));
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        if (room is not null)
            cmd.Parameters.Add(new DuckDBParameter("room", room));

        var list = new List<Drawer>();
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (reader.Read())
        {
            list.Add(new Drawer(
                Id: reader.GetString(0), Wing: reader.GetString(1),
                Room: reader.GetString(2), Content: reader.GetString(3),
                Source: reader.IsDBNull(4) ? null : reader.GetString(4),
                FtsText: null, EmbeddingText: null, ParentId: null,
                ContentHash: string.Empty, SourceMtime: null,
                Importance: 1f, DrawerType: "source",
                MinedAt: new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero),
                ValidFrom: DateTimeOffset.UtcNow, ValidTo: null
            ));
        }
        return list;
    }

    public async Task<PruneReport> PruneAsync(string wing, float threshold, CancellationToken ct)
    {
        var drawers = await LoadEmbeddingsForWingAsync(wing, ct);
        if (drawers.Count == 0)
            return new PruneReport(0, 0, 0);

        float thetaPrime = 1f - threshold;
        var clusters = BuildClusters(drawers, threshold);

        var toRetire = new List<string>();
        var toKeep = new List<string>();

        foreach (var (_, clusterMembers) in clusters)
        {
            var members = clusterMembers;
            // Protect frequently-accessed drawers from pruning
            if (palaceConfig.PruneAccessProtectionThreshold > 0)
            {
                var protectedIds = await LoadProtectedIdsAsync(wing, ct);
                members = members
                    .Where(d => !protectedIds.Contains(d.Id))
                    .ToList();
                if (members.Count == 0)
                    continue;
            }

            if (members.Count == 1)
            { toKeep.Add(members[0].Id); continue; }
            var (dBar, _) = ClusterGeometry(members);

            if (dBar < thetaPrime)
            {
                var medoid = FindMedoid(members);
                toRetire.AddRange(members.Select(m => m.Id).Where(id => id != medoid.Id));
                toKeep.Add(medoid.Id);
            }
            else
            {
                int m = Math.Min(members.Count, (int)Math.Ceiling(dBar / thetaPrime));
                var reps = GreedyMedoidSelection(members, m);
                var repIds = reps.Select(r => r.Id).ToHashSet();
                toRetire.AddRange(members.Select(m2 => m2.Id).Where(id => !repIds.Contains(id)));
                toKeep.AddRange(repIds);
            }
        }

        if (toRetire.Count > 0)
        {
            await dbFactory.ExecuteWriteAsync(db =>
            {
                using var cmd = db.CreateCommand();
                foreach (var retireId in toRetire)
                {
                    using var updateCmd = db.CreateCommand();
                    updateCmd.CommandText = """
                        UPDATE drawers
                        SET is_representative = FALSE
                        WHERE id = $id
                          AND wing = $wing
                        """;
                    updateCmd.Parameters.Add(new DuckDBParameter("id", retireId));
                    updateCmd.Parameters.Add(new DuckDBParameter("wing", wing));
                    updateCmd.ExecuteNonQuery();
                }
                return Task.CompletedTask;
            }, ct);
        }

        await RebuildFtsIndexAsync(ct);

        if (activityLog is not null)
            await activityLog.LogAsync("prune", wing, null, null,
                $"{clusters.Count} clusters, {toRetire.Count} retired, {toKeep.Count} kept", ct);

        return new PruneReport(clusters.Count, toRetire.Count, toKeep.Count);
    }

    static IReadOnlyList<string> RrfFuse(
        IEnumerable<IReadOnlyList<string>> lists, int k = 60)
    {
        var scores = new Dictionary<string, double>();
        foreach (var list in lists)
            foreach (var (id, rank) in list.Select((id, i) => (id, i)))
                scores[id] = scores.GetValueOrDefault(id) + 1.0 / (k + rank + 1);
        return [.. scores.OrderByDescending(kv => kv.Value).Select(kv => kv.Key)];
    }

    IReadOnlyList<DrawerResult> SearchDelta(string query, string? wing)
    {
        var terms = query.ToLowerInvariant()
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        List<(string Id, string Wing, string Room, string Content, int Hits)> hits;
        lock (_deltaLock)
        {
            hits = _delta
                .Where(d => wing is null || d.Wing == wing)
                .Select(d =>
                {
                    var text = d.FtsText.ToLowerInvariant();
                    int count = terms.Count(t => text.Contains(t));
                    return (d.Id, d.Wing, d.Room, d.Content, count);
                })
                .Where(x => x.count > 0)
                .OrderByDescending(x => x.count)
                .ToList();
        }

        return hits.Select(h => new DrawerResult(
            new Drawer(h.Id, h.Wing, h.Room, h.Content,
                       null, null, null, string.Empty, null, null,
                       1f, "source", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null),
            Score: (float)h.Hits / terms.Length,
            Regime: ClusterRegime.Unknown
        )).ToList();
    }

    async Task<List<DrawerResult>> ReadResultsAsync(DuckDBCommand cmd, CancellationToken ct)
    {
        var results = new List<DrawerResult>();
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (reader.Read())
        {
            var accessCount = reader.FieldCount > 6 && !reader.IsDBNull(6) ? reader.GetInt32(6) : 0;
            var lastAccessed = reader.FieldCount > 7 && !reader.IsDBNull(7) ? reader.GetDateTime(7) : (DateTime?)null;
            var drawer = new Drawer(
                Id: reader.GetString(0),
                Wing: reader.GetString(1),
                Room: reader.GetString(2),
                Content: reader.GetString(3),
                Source: reader.IsDBNull(4) ? null : reader.GetString(4),
                FtsText: null, EmbeddingText: null, ParentId: null,
                ContentHash: string.Empty, SourceMtime: null,
                Importance: 1f, DrawerType: "source",
                MinedAt: DateTimeOffset.UtcNow,
                ValidFrom: DateTimeOffset.UtcNow, ValidTo: null,
                LastAccessedAt: lastAccessed.HasValue ? new DateTimeOffset(lastAccessed.Value, TimeSpan.Zero) : null,
                AccessCount: accessCount
            );
            var score = reader.IsDBNull(5) ? 0f : Convert.ToSingle(reader.GetValue(5));
            results.Add(new DrawerResult(drawer, score, ClusterRegime.Unknown));
        }
        return results;
    }

    /// <summary>
    /// Read drawer results including the importance column (column index 8).
    /// Used by HybridSearchAsync to apply salience scoring.
    /// </summary>
    async Task<List<DrawerResult>> ReadResultsWithImportanceAsync(DuckDBCommand cmd, CancellationToken ct)
    {
        var results = new List<DrawerResult>();
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (reader.Read())
        {
            var accessCount = reader.FieldCount > 6 && !reader.IsDBNull(6) ? reader.GetInt32(6) : 0;
            var lastAccessed = reader.FieldCount > 7 && !reader.IsDBNull(7) ? reader.GetDateTime(7) : (DateTime?)null;
            var importance = reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetFloat(8) : 1f;
            var drawer = new Drawer(
                Id: reader.GetString(0),
                Wing: reader.GetString(1),
                Room: reader.GetString(2),
                Content: reader.GetString(3),
                Source: reader.IsDBNull(4) ? null : reader.GetString(4),
                FtsText: null, EmbeddingText: null, ParentId: null,
                ContentHash: string.Empty, SourceMtime: null,
                Importance: importance, DrawerType: "source",
                MinedAt: DateTimeOffset.UtcNow,
                ValidFrom: DateTimeOffset.UtcNow, ValidTo: null,
                LastAccessedAt: lastAccessed.HasValue ? new DateTimeOffset(lastAccessed.Value, TimeSpan.Zero) : null,
                AccessCount: accessCount
            );
            var score = reader.IsDBNull(5) ? 0f : Convert.ToSingle(reader.GetValue(5));
            results.Add(new DrawerResult(drawer, score, ClusterRegime.Unknown));
        }
        return results;
    }

    /// <summary>
    /// Add importance * weight to each drawer's score after normalization.
    /// Salience is a tiebreaker — the weight should be small (default 0.10).
    /// </summary>
    static List<DrawerResult> ApplySalienceBoost(List<DrawerResult> results, float weight)
    {
        for (int i = 0; i < results.Count; i++)
        {
            var importance = results[i].Drawer.Importance;
            if (importance != 1f && importance > 0f)
            {
                results[i] = results[i] with { Score = results[i].Score + weight * importance };
            }
        }
        return results;
    }

    internal static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = TensorPrimitives.Dot(a, b);
        float normA = TensorPrimitives.Norm(a);
        float normB = TensorPrimitives.Norm(b);
        float denom = normA * normB;
        return denom < 1e-9f ? 0f : dot / denom;
    }

    static string ComputeId(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    static string ComputeHash(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    static string BuildFtsText(string content, string wing, string room)
        => $"{content} {wing} {room}";

    // Minimal projection used inside DrawerStorage — no exposure outside.
    internal record EmbeddingRow(string Id, float[] Embedding);

    private void ActivityLogFireAndForget(string action, string? wing, string? room, string summary)
    {
        if (activityLog is null)
            return;
        _ = Task.Run(async () =>
        {
            try
            { await activityLog.LogAsync(action, wing, room, null, summary); }
            catch { /* fire-and-forget */ }
        });
    }

    private async Task<AdmissionResult> ShouldAdmitAsync(string text, float[]? embedding, string wing, CancellationToken ct)
    {
        if (text.Trim().Length < 80)
            return new AdmissionResult(null, false, "content too short", null);

        var alphaCount = text.Count(char.IsLetterOrDigit);
        var nonAlphaCount = text.Length - alphaCount;
        if (text.Length > 0 && (float)nonAlphaCount / text.Length > 0.95f)
            return new AdmissionResult(null, false, "low signal ratio", null);

        try
        {
            using var ro = dbFactory.OpenReadOnly();
            using var cmd = ro.CreateCommand();
            if (embedding is null)
                embedding = await embedder.EmbedDocumentAsync(text);
            var lit = EmbeddingUtils.ToFloatArrayLiteral(embedding) + "::FLOAT[" + embedding.Length + "]";
            cmd.CommandText = "SELECT id, array_cosine_similarity(embedding, " + lit + ") FROM drawers WHERE wing = $wing AND embedding IS NOT NULL ORDER BY 2 DESC LIMIT 1";
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
            using var dbr = cmd.ExecuteReader();
            if (dbr.Read() && !dbr.IsDBNull(1))
            {
                var matchedId = dbr.GetString(0);
                var maxSim = dbr.GetFloat(1);
                if (maxSim >= palaceConfig.AdmissionDuplicateThreshold)
                    return new AdmissionResult(null, false, "near_duplicate", matchedId);
            }
        }
        catch { /* if similarity check fails, admit */ }

        return new AdmissionResult(null, true, null, null);
    }

    /// Record that drawers were accessed (fire-and-forget for scoring).
    /// Batches all IDs into a single UPDATE statement under one write lock acquisition.
    public void RecordAccess(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await dbFactory.ExecuteWriteAsync(async db =>
                {
                    var paramNames = string.Join(",", ids.Select((_, i) => $"$aid{i}"));
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = $"""
                        UPDATE drawers
                        SET access_count = access_count + 1,
                            last_accessed_at = now()
                        WHERE id IN ({paramNames})
                        """;
                    for (int i = 0; i < ids.Count; i++)
                        cmd.Parameters.Add(new DuckDBParameter($"aid{i}", ids[i]));
                    await cmd.ExecuteNonQueryAsync();
                });
            }
            catch { /* fire-and-forget */ }
        });
    }

    private async Task<HashSet<string>> LoadProtectedIdsAsync(string wing, CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM drawers
            WHERE wing = $wing
              AND access_count >= $threshold
              AND valid_to IS NULL
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        cmd.Parameters.Add(new DuckDBParameter("threshold", palaceConfig.PruneAccessProtectionThreshold));
        var ids = new HashSet<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetString(0));
        return ids;
    }
}
