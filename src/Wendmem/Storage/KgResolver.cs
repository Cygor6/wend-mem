using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Wendmem.Experiences;
using Wendmem.Serialization;
using Wendmem.Services;

namespace Wendmem.Storage;

sealed class KgResolver(
    DuckDbConnectionFactory dbFactory,
    IEmbedder embedder,
    IConfiguration config,
    LlmService llm,
    ILogger<KgResolver> logger)
{
    const int TopK = 16;
    const float EntityThreshold = 0.82f;
    const float PredicateThreshold = 0.85f;

    public async Task<KgResolveResult> ResolveAsync(string wing, CancellationToken ct)
    {
        var (entitiesMerged, triplesRedirected) = await ResolveEntitiesAsync(wing, ct);
        var predicatesNormalized = await NormalizePredicatesAsync(wing, ct);
        var confidenceUpdated = await UpdateConfidenceAsync(wing, ct);
        return new KgResolveResult(entitiesMerged, triplesRedirected, predicatesNormalized, confidenceUpdated.Merged, confidenceUpdated.MinConf, confidenceUpdated.MaxConf);
    }

    // Phase 1: Entity Resolution
    async Task<(int Merged, int Redirected)> ResolveEntitiesAsync(string wing, CancellationToken ct)
    {
        var entities = await LoadEntityEmbeddingsAsync(wing, ct);
        if (entities.Count < 2)
            return (0, 0);

        var groups = await FindCandidateGroupsAsync(entities, EntityThreshold, ct);
        int totalMerged = 0, totalRedirected = 0;

        foreach (var group in groups)
        {
            var names = group.Select(g => g.Name).ToList();
            var (canonical, aliases) = await ConfirmWithLlmAsync(names, wing, "entity", ct);
            if (canonical is null || aliases.Count == 0)
                continue;

            var canonicalItem = group.First(g => g.Name.Equals(canonical, StringComparison.OrdinalIgnoreCase));
            var aliasItems = group.Where(g => aliases.Contains(g.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            aliasItems = aliasItems.Where(a => a.Id != canonicalItem.Id).ToList();
            if (aliasItems.Count == 0)
                continue;

            await MergeEntitiesAsync(
                canonicalItem.Id, canonicalItem.Name,
                aliasItems.Select(a => a.Id).ToList(),
                aliasItems.Select(a => a.Name).ToList(), ct);

            totalMerged += aliasItems.Count;
            totalRedirected += aliasItems.Count;
        }

        return (totalMerged, totalRedirected);
    }

    async Task<List<(string Id, string Name, float[] Embedding)>> LoadEntityEmbeddingsAsync(
        string wing, CancellationToken ct)
    {
        var names = new List<(string Id, string Name)>();
        using (var ro = dbFactory.OpenReadOnly())
        {
            using var cmd = ro.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT e.id, e.name
                FROM entities e
                JOIN triples t ON t.subject = e.id OR t.object = e.id
                JOIN drawers d ON d.id = t.drawer_id
                WHERE d.wing = $wing AND t.valid_to IS NULL
                """;
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
            using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
            while (reader.Read())
                names.Add((reader.GetString(0), reader.GetString(1)));
        }

        if (names.Count == 0)
            return [];
        var nameArrays = names.Select(n => n.Name).ToList();
        var embeddings = await embedder.EmbedDocumentBatchAsync(nameArrays, ct);
        return names.Zip(embeddings, (n, emb) => (n.Id, n.Name, emb)).ToList();
    }

    // Fix: mark i as assigned immediately when it becomes a group anchor.
    public async Task<List<List<(string Id, string Name, float[] Embedding)>>>
        FindCandidateGroupsAsync(
            IReadOnlyList<(string Id, string Name, float[] Embedding)> items,
            float threshold, CancellationToken ct)
    {
        var groups = new List<List<(string Id, string Name, float[] Embedding)>>();
        var assigned = new HashSet<int>();

        for (int i = 0; i < items.Count; i++)
        {
            if (assigned.Contains(i))
                continue;

            assigned.Add(i);

            var group = new List<(string Id, string Name, float[] Embedding)> { items[i] };
            var similarities = new List<(int Index, float Score)>();
            for (int j = 0; j < items.Count; j++)
            {
                if (j == i || assigned.Contains(j))
                    continue;
                float score = CosineSimilarity(items[i].Embedding, items[j].Embedding);
                if (score >= threshold)
                    similarities.Add((j, score));
            }

            foreach (var (idx, _) in similarities.OrderByDescending(s => s.Score).Take(TopK))
            {
                assigned.Add(idx);
                group.Add(items[idx]);
            }

            if (group.Count > 1)
                groups.Add(group);
        }

        return groups;
    }

    async Task<(string? Canonical, List<string> Aliases)> ConfirmWithLlmAsync(
        List<string> candidateNames, string wing, string itemType, CancellationToken ct)
    {
        var systemPrompt = itemType == "entity"
            ? "You are a knowledge graph curator. Identify which of these entity names refer to the exact same real-world entity. Consider abbreviations, casing, version suffixes, and aliases. Respond ONLY with valid JSON, no markdown, no preamble."
            : "You are a knowledge graph curator. Identify which of these predicate strings express the exact same directed relationship. \"uses\" and \"is used by\" are NOT the same - direction matters. Respond ONLY with valid JSON, no markdown, no preamble.";

        var label = itemType == "entity" ? "Entities" : "Predicates";
        var namesJson = JsonSerializer.Serialize(candidateNames, WendmemJsonContext.Default.ListString);
        var userPrompt = itemType == "entity"
            ? FormattableString.Invariant($"{label}: {namesJson}\nContext: {wing}\nReturn: {{\"canonical\": \"<name>\", \"aliases\": [\"<alias1>\", ...]}}\nIf none are duplicates: {{\"canonical\": null, \"aliases\": []}}")
            : FormattableString.Invariant($"{label}: {namesJson}\nReturn: {{\"canonical\": \"<predicate>\", \"aliases\": [\"<alias1>\", ...]}}\nIf none are true duplicates: {{\"canonical\": null, \"aliases\": []}}");

        try
        {
            var prompt = $"{systemPrompt}\n\n{userPrompt}";
            var content = await llm.CompleteAsync(prompt, ct);

            if (content.Contains("```"))
            {
                var start = content.IndexOf('{');
                var end = content.LastIndexOf('}');
                if (start >= 0 && end > start)
                    content = content[start..(end + 1)];
            }

            if (string.IsNullOrWhiteSpace(content))
                return (null, []);

            using var result = JsonDocument.Parse(content);
            var canonicalEl = result.RootElement.GetProperty("canonical");
            var canonical = canonicalEl.ValueKind == JsonValueKind.Null ? null : canonicalEl.GetString();

            var aliases = new List<string>();
            if (result.RootElement.TryGetProperty("aliases", out var aliasesEl))
            {
                foreach (var a in aliasesEl.EnumerateArray())
                    aliases.Add(a.GetString() ?? "");
            }

            return (canonical, aliases);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ConfirmWithLlmAsync failed for {ItemType} in wing {Wing}", itemType, wing);
            return (null, []);
        }
    }

    public async Task MergeEntitiesAsync(
        string canonicalId, string canonicalName,
        List<string> aliasIds, List<string> aliasNames,
        CancellationToken ct)
    {
        await dbFactory.ExecuteWriteAsync(async db =>
        {
            foreach (var aliasId in aliasIds)
            {
                using var subjectCmd = db.CreateCommand();
                subjectCmd.CommandText = "UPDATE triples SET subject = $canonical WHERE subject = $alias";
                subjectCmd.Parameters.Add(new DuckDBParameter("canonical", canonicalId));
                subjectCmd.Parameters.Add(new DuckDBParameter("alias", aliasId));
                await Task.Run(() => subjectCmd.ExecuteNonQuery(), ct);

                using var objectCmd = db.CreateCommand();
                objectCmd.CommandText = "UPDATE triples SET object = $canonical WHERE object = $alias";
                objectCmd.Parameters.Add(new DuckDBParameter("canonical", canonicalId));
                objectCmd.Parameters.Add(new DuckDBParameter("alias", aliasId));
                await Task.Run(() => objectCmd.ExecuteNonQuery(), ct);

                using var delCmd = db.CreateCommand();
                delCmd.CommandText = "DELETE FROM entities WHERE id = $alias";
                delCmd.Parameters.Add(new DuckDBParameter("alias", aliasId));
                await Task.Run(() => delCmd.ExecuteNonQuery(), ct);
            }

            // Store aliases in canonical entity properties
            using var propsCmd = db.CreateCommand();
            propsCmd.CommandText = "SELECT properties FROM entities WHERE id = $id";
            propsCmd.Parameters.Add(new DuckDBParameter("id", canonicalId));
            var propsRaw = await Task.Run(() => propsCmd.ExecuteScalar(), ct);

            var props = (propsRaw is DBNull or null)
                ? new Dictionary<string, object>()
                : JsonSerializer.Deserialize(propsRaw.ToString()!, WendmemJsonContext.Default.DictionaryStringObject);
            props["aliases"] = aliasNames;

            var mergedJson = JsonSerializer.Serialize(props, WendmemJsonContext.Default.DictionaryStringObject);
            using var updateCmd = db.CreateCommand();
            updateCmd.CommandText = "UPDATE entities SET properties = TRY_CAST($mergedJson AS JSON) WHERE id = $id";
            updateCmd.Parameters.Add(new DuckDBParameter("mergedJson", mergedJson));
            updateCmd.Parameters.Add(new DuckDBParameter("id", canonicalId));
            await Task.Run(() => updateCmd.ExecuteNonQuery(), ct);
        }, ct);
    }

    // Phase 2: Predicate Normalization
    async Task<int> NormalizePredicatesAsync(string wing, CancellationToken ct)
    {
        var predicates = await LoadPredicateEmbeddingsAsync(ct);
        if (predicates.Count < 2)
            return 0;

        var items = predicates.Select(p => ("", p.Name, p.Embedding)).ToList();
        var groups = await FindCandidateGroupsAsync(items, PredicateThreshold, ct);

        int total = 0;
        foreach (var group in groups)
        {
            var names = group.Select(g => g.Name).ToList();
            var (canonical, aliases) = await ConfirmWithLlmAsync(names, wing, "predicate", ct);
            if (canonical is null || aliases.Count == 0)
                continue;

            await dbFactory.ExecuteWriteAsync(async db =>
            {
                foreach (var alias in aliases)
                {
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "UPDATE triples SET predicate = $canonical WHERE predicate = $alias";
                    cmd.Parameters.Add(new DuckDBParameter("canonical", canonical));
                    cmd.Parameters.Add(new DuckDBParameter("alias", alias));
                    total += await Task.Run(() => cmd.ExecuteNonQuery(), ct);
                }
            }, ct);
        }

        return total;
    }

    async Task<List<(string Name, float[] Embedding)>> LoadPredicateEmbeddingsAsync(
        CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var ro = dbFactory.OpenReadOnly())
        {
            using var cmd = ro.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT predicate FROM triples WHERE valid_to IS NULL
                """;
            using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
            while (reader.Read())
                names.Add(reader.GetString(0));
        }

        if (names.Count == 0)
            return [];
        var nameList = names.ToList();
        var embeddings = await embedder.EmbedDocumentBatchAsync(nameList, ct);
        return nameList.Zip(embeddings, (n, e) => (n, e)).ToList();
    }

    // Phase 3: Entity Frequency -> Confidence
    async Task<(int Merged, double MinConf, double MaxConf)> UpdateConfidenceAsync(string wing, CancellationToken ct)
    {
        var mentionCounts = new List<(string EntityId, int MentionCount)>();
        using (var ro = dbFactory.OpenReadOnly())
        {
            using var cmd = ro.CreateCommand();
            cmd.CommandText = """
                SELECT e.id, COUNT(DISTINCT d.id) AS mention_count
                FROM entities e
                JOIN triples t ON t.subject = e.id OR t.object = e.id
                JOIN drawers d ON d.id = t.drawer_id
                WHERE d.wing = $wing AND t.valid_to IS NULL
                GROUP BY e.id
                """;
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
            using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
            while (reader.Read())
                mentionCounts.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        int updated = 0;
        foreach (var (entityId, mentionCount) in mentionCounts)
        {
            var confidence = Math.Min(1.0f, 0.5f + (mentionCount - 1) * 0.075f);

            await dbFactory.ExecuteWriteAsync(async db =>
            {
                // Update entity properties (preserve existing keys like aliases)
                using var propsCmd = db.CreateCommand();
                propsCmd.CommandText = "SELECT properties FROM entities WHERE id = $id";
                propsCmd.Parameters.Add(new DuckDBParameter("id", entityId));
                var propsRaw = await Task.Run(() => propsCmd.ExecuteScalar(), ct);

                var props = (propsRaw is DBNull or null)
                    ? new Dictionary<string, object>()
                    : JsonSerializer.Deserialize(propsRaw.ToString()!, WendmemJsonContext.Default.DictionaryStringObject);
                props["mention_count"] = mentionCount;
                props["confidence"] = confidence;

                var mergedJson = JsonSerializer.Serialize(props, WendmemJsonContext.Default.DictionaryStringObject);
                using var updatePropsCmd = db.CreateCommand();
                updatePropsCmd.CommandText = "UPDATE entities SET properties = TRY_CAST($mergedJson AS JSON) WHERE id = $id";
                updatePropsCmd.Parameters.Add(new DuckDBParameter("mergedJson", mergedJson));
                updatePropsCmd.Parameters.Add(new DuckDBParameter("id", entityId));
                await Task.Run(() => updatePropsCmd.ExecuteNonQuery(), ct);

                // Update confidence on active triples (time-decayed per triple)
                using var tripleCmd = db.CreateCommand();
                tripleCmd.CommandText = """
                    UPDATE triples SET confidence =
                        GREATEST(0.05, LEAST(1.0,
                            $baseScore * EXP(-DATE_DIFF('day', valid_from, CURRENT_TIMESTAMP)::DOUBLE / 180.0)))
                    WHERE (subject = $entityId OR object = $entityId)
                      AND valid_to IS NULL
                    """;
                tripleCmd.Parameters.Add(new DuckDBParameter("baseScore", (double)confidence));
                tripleCmd.Parameters.Add(new DuckDBParameter("entityId", entityId));
                await Task.Run(() => tripleCmd.ExecuteNonQuery(), ct);
            }, ct);

            updated++;
        }

        double minConf = 0.05, maxConf = 1.0;
        if (mentionCounts.Count > 0)
        {
            using var ro = dbFactory.OpenReadOnly();
            using var rangeCmd = ro.CreateCommand();
            var inParams = string.Join(" OR ",
                mentionCounts.Select((m, i) => $"subject = $rid{i} OR object = $rid{i}"));
            rangeCmd.CommandText = $"""
                SELECT MIN(confidence), MAX(confidence)
                FROM triples
                WHERE valid_to IS NULL AND ({inParams})
                """;
            for (int i = 0; i < mentionCounts.Count; i++)
                rangeCmd.Parameters.Add(new DuckDBParameter($"rid{i}", mentionCounts[i].EntityId));
            using var rangeReader = await Task.Run(() => rangeCmd.ExecuteReader(), ct);
            if (await Task.Run(() => rangeReader.Read(), ct))
            {
                minConf = rangeReader.IsDBNull(0) ? 0.05 : rangeReader.GetDouble(0);
                maxConf = rangeReader.IsDBNull(1) ? 1.0 : rangeReader.GetDouble(1);
            }
        }

        return (updated, minConf, maxConf);
    }

    static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denom == 0 ? 0 : dot / denom;
    }
}

record KgResolveResult(
    int EntitiesMerged,
    int TriplesRedirected,
    int PredicatesNormalized,
    int ConfidenceUpdated,
    double ConfidenceMin,
    double ConfidenceMax);
