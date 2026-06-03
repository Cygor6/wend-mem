using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Wendmem.Models;
using Wendmem.Services;

namespace Wendmem.Storage;

sealed partial class KnowledgeGraph(DuckDbConnectionFactory dbFactory, Services.ActivityLog? activityLog = null)
{
    // KGGen-inspired predicate canonicalization: maps common aliases
    // to a single canonical predicate, reducing KG sparsity from
    // predicate duplication (NeurIPS '25, Stanford).
    static readonly Dictionary<string, string> PredicateAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uses_tool"] = "uses",
        ["uses_library"] = "uses",
        ["utilizes"] = "uses",
        ["depends_on"] = "depends_on",
        ["dependency_of"] = "depends_on",
        ["has_dependency"] = "depends_on",
        ["works_for"] = "works_at",
        ["employed_by"] = "works_at",
        ["employed_at"] = "works_at",
        ["is_part_of"] = "part_of",
        ["belongs_to"] = "part_of",
        ["member_of"] = "part_of",
        ["is_a"] = "is_type",
        ["type_of"] = "is_type",
        ["instance_of"] = "is_type",
        ["located_in"] = "located_in",
        ["lives_in"] = "located_in",
        ["resides_in"] = "located_in",
        ["has_version"] = "has_version",
        ["version"] = "has_version",
        ["version_of"] = "has_version",
    };

    static string Canonicalize(string name)
        => NonAsciiRegex().Replace(
            LowerStripRegex().Replace(name.ToLowerInvariant(), ""), "");

    [GeneratedRegex(@"[^\x20-\x7E]+")]
    private static partial Regex NonAsciiRegex();

    [GeneratedRegex(@"[\s\-_]+")]
    private static partial Regex LowerStripRegex();

    static string CanonicalizePredicate(string predicate)
    {
        var normalized = PredicateNormalizeRegex().Replace(predicate.ToLowerInvariant(), "_");
        return PredicateAliases.TryGetValue(normalized, out var canonical) ? canonical : normalized;
    }

    [GeneratedRegex(@"[\s\-]+")]
    private static partial Regex PredicateNormalizeRegex();

    public async Task<string> AddEntityAsync(
        string name, string entityType,
        string? propertiesJson,
        CancellationToken ct)
    {
        var canonical = Canonicalize(name);
        var id = EntityId(canonical, entityType);

        await dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO entities (id, name, canonical_name, type, properties)
                VALUES ($id, $name, $canonical, $type, $props::JSON)
                ON CONFLICT (canonical_name) DO UPDATE SET
                    name       = excluded.name,
                    type       = COALESCE(entities.type, excluded.type),
                    properties = CASE
                        WHEN excluded.properties = '{}'::JSON THEN entities.properties
                        ELSE excluded.properties
                    END
                """;
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            cmd.Parameters.Add(new DuckDBParameter("name", name));
            cmd.Parameters.Add(new DuckDBParameter("canonical", canonical));
            cmd.Parameters.Add(new DuckDBParameter("type", entityType));
            cmd.Parameters.Add(new DuckDBParameter("props", propertiesJson ?? "{}"));
            await Task.Run(() => cmd.ExecuteNonQuery(), ct);
        }, ct);
        return id;
    }

    /// <summary>
    /// KGGen fix: resolve an entity name to its ID, creating the entity
    /// if it doesn't exist. Eliminates dual-track IDs.
    /// </summary>
    public async Task<string> EnsureEntityAsync(string name, string type, CancellationToken ct)
    {
        var canonical = Canonicalize(name);
        var existingId = await ResolveEntityIdByCanonicalAsync(canonical, ct);
        if (existingId is not null)
            return existingId;

        await AddEntityAsync(name, type, null, ct);
        // Re-resolve: upsert may keep original row's id if created concurrently
        // with a different type.
        return (await ResolveEntityIdByCanonicalAsync(canonical, ct))!;
    }

    public async Task<(string Id, Models.ConflictInfo? Conflict)> AddTripleAsync(
        string subject, string predicate, string obj,
        DateOnly? validFrom = null,
        DateOnly? validTo = null,
        double confidence = 1.0,
        string? sourceRoom = null,
        string? sourceFile = null,
        string? drawerId = null,
        CancellationToken ct = default)
    {
        // auto-create entities and canonicalize predicate
        var subjectId = await EnsureEntityAsync(subject, EntityClassifier.Classify(subject), ct);
        var objectId = await EnsureEntityAsync(obj, EntityClassifier.Classify(obj), ct);
        var canonPredicate = CanonicalizePredicate(predicate);

        var from = validFrom ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var id = TripleId(subjectId, canonPredicate, objectId, from);

        await dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO triples
                    (id, subject, predicate, object, valid_from, valid_to,
                     confidence, source_room, source_file, drawer_id)
                VALUES
                    ($id, $subject, $predicate, $object, $valid_from, $valid_to,
                     $confidence, $source_room, $source_file, $drawer_id)
                ON CONFLICT (id) DO NOTHING
                """;
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            cmd.Parameters.Add(new DuckDBParameter("subject", subjectId));
            cmd.Parameters.Add(new DuckDBParameter("predicate", canonPredicate));
            cmd.Parameters.Add(new DuckDBParameter("object", objectId));
            cmd.Parameters.Add(new DuckDBParameter("valid_from", from.ToString("yyyy-MM-dd")));
            cmd.Parameters.Add(new DuckDBParameter("valid_to", validTo?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("confidence", confidence));
            cmd.Parameters.Add(new DuckDBParameter("source_room", sourceRoom ?? (object)DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("source_file", sourceFile ?? (object)DBNull.Value));
            cmd.Parameters.Add(new DuckDBParameter("drawer_id", drawerId ?? (object)DBNull.Value));
            await Task.Run(() => cmd.ExecuteNonQuery(), ct);
        }, ct);

        var conflict = await CheckConflictAsync(subjectId, subject, canonPredicate, objectId, ct);

        if (activityLog is not null)
            await activityLog.LogAsync("add_triple", null, $"{subject} {predicate} {obj}", null,
                $"Added triple: {subject} {predicate} {obj}", ct);

        return (id, conflict);
    }

    async Task<Models.ConflictInfo?> CheckConflictAsync(
        string subjectId, string subjectName,
        string canonPredicate,
        string objectId,
        CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT t.id, t.valid_from, e.name AS object_name
            FROM triples t
            JOIN entities e ON e.id = t.object
            WHERE t.subject = $subjectId
              AND t.predicate = $predicate
              AND t.object != $objectId
              AND t.valid_to IS NULL
            """;
        cmd.Parameters.Add(new DuckDBParameter("subjectId", subjectId));
        cmd.Parameters.Add(new DuckDBParameter("predicate", canonPredicate));
        cmd.Parameters.Add(new DuckDBParameter("objectId", objectId));

        var conflicts = new List<(string Id, string ObjName, DateOnly From)>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            conflicts.Add((
                reader.GetString(0),
                reader.GetString(2),
                DateOnly.FromDateTime(reader.GetDateTime(1))));

        if (conflicts.Count != 1)
            return null;

        return new Models.ConflictInfo(
            TripleId: conflicts[0].Id,
            Subject: subjectName,
            Predicate: canonPredicate,
            Object: conflicts[0].ObjName,
            ValidFrom: conflicts[0].From,
            Message: $"Active triple with same subject+predicate exists. " +
                       $"Call InvalidateTriple(\"{subjectName}\", \"{canonPredicate}\", " +
                       $"\"{conflicts[0].ObjName}\") if this fact has changed.");
    }

    async Task<string?> ResolveEntityIdAsync(string name, CancellationToken ct)
    {
        var canonical = Canonicalize(name);
        return await ResolveEntityIdByCanonicalAsync(canonical, ct);
    }

    async Task<string?> ResolveEntityIdByCanonicalAsync(string canonical, CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = "SELECT id FROM entities WHERE canonical_name = $canonical LIMIT 1";
        cmd.Parameters.Add(new DuckDBParameter("canonical", canonical));
        var result = await Task.Run(() => cmd.ExecuteScalar(), ct);
        return result is DBNull or null ? null : (string)result;
    }

    public async Task InvalidateAsync(
        string subject, string predicate, string obj,
        DateOnly? ended = null,
        CancellationToken ct = default)
    {
        var subjectId = await EnsureEntityAsync(subject, EntityClassifier.Classify(subject), ct);
        var objectId = await EnsureEntityAsync(obj, EntityClassifier.Classify(obj), ct);
        var canonPredicate = CanonicalizePredicate(predicate);
        var date = (ended ?? DateOnly.FromDateTime(DateTime.UtcNow)).ToString("yyyy-MM-dd");

        await dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE triples
                SET valid_to = $ended
                WHERE subject   = $subject
                  AND predicate = $predicate
                  AND object    = $object
                  AND valid_to IS NULL
                """;
            cmd.Parameters.Add(new DuckDBParameter("ended", date));
            cmd.Parameters.Add(new DuckDBParameter("subject", subjectId));
            cmd.Parameters.Add(new DuckDBParameter("predicate", canonPredicate));
            cmd.Parameters.Add(new DuckDBParameter("object", objectId));
            await Task.Run(() => cmd.ExecuteNonQuery(), ct);
        }, ct);

        if (activityLog is not null)
            await activityLog.LogAsync("invalidate_triple", null, $"{subject} {canonPredicate} {obj}", null,
                $"Invalidated triple: {subject} {canonPredicate} {obj}", ct);
    }

    public async Task<IReadOnlyList<Triple>> QueryEntityAsync(
        string name,
        DateOnly? asOf = null,
        TripleDirection dir = TripleDirection.Both,
        CancellationToken ct = default)
    {
        var date = (asOf ?? DateOnly.FromDateTime(DateTime.UtcNow)).ToString("yyyy-MM-dd");

        using var ro = dbFactory.OpenReadOnly();
        using var idCmd = ro.CreateCommand();
        idCmd.CommandText = "SELECT id FROM entities WHERE canonical_name = $canonical LIMIT 1";
        idCmd.Parameters.Add(new DuckDBParameter("canonical", Canonicalize(name)));
        var entityId = await Task.Run(() => idCmd.ExecuteScalar(), ct) as string;
        if (entityId is null)
            return [];

        var dirFilter = dir switch
        {
            TripleDirection.Outgoing => "t.subject = $eid",
            TripleDirection.Incoming => "t.object  = $eid",
            _ => "(t.subject = $eid OR t.object = $eid)",
        };

        using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT t.subject, t.predicate, t.object,
                   t.valid_from, t.valid_to, t.confidence,
                   t.source_room, t.source_file
            FROM triples t
            WHERE {dirFilter}
              AND t.valid_from <= $at_date
              AND (t.valid_to IS NULL OR t.valid_to > $at_date)
            ORDER BY t.valid_from DESC
            """;
        cmd.Parameters.Add(new DuckDBParameter("eid", entityId));
        cmd.Parameters.Add(new DuckDBParameter("at_date", date));

        var list = new List<Triple>();
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (reader.Read())
        {
            list.Add(new Triple(
                Subject: reader.GetString(0),
                Predicate: reader.GetString(1),
                Object: reader.GetString(2),
                ValidFrom: reader.GetDateTime(3) == default ? DateOnly.FromDateTime(DateTime.UtcNow) : DateOnly.FromDateTime(reader.GetDateTime(3)),
                ValidTo: reader.IsDBNull(4) ? null : DateOnly.FromDateTime(reader.GetDateTime(4)),
                Confidence: Convert.ToDouble(reader.GetValue(5)),
                SourceRoom: reader.IsDBNull(6) ? null : reader.GetString(6),
                SourceFile: reader.IsDBNull(7) ? null : reader.GetString(7)
            ));
        }
        return list;
    }

    public async Task<KgStats> StatsAsync(CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT count(*) FROM entities)                        AS entity_count,
              (SELECT count(*) FROM triples)                        AS triple_count,
              (SELECT count(*) FROM triples WHERE valid_to IS NULL) AS active_count
            """;
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        reader.Read();
        return new KgStats(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    public async Task<string> AddTunnelAsync(
        string topic,
        string wingA, string roomA,
        string wingB, string roomB,
        CancellationToken ct)
    {
        // Normalise order so (A,B) and (B,A) produce the same id
        var (wa, ra, wb, rb) = string.Compare($"{wingA}/{roomA}", $"{wingB}/{roomB}", StringComparison.Ordinal) <= 0
            ? (wingA, roomA, wingB, roomB)
            : (wingB, roomB, wingA, roomA);

        var id = TunnelId(topic, wa, ra, wb, rb);

        await dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tunnels (id, topic, wing_a, room_a, wing_b, room_b)
                VALUES ($id, $topic, $wa, $ra, $wb, $rb)
                ON CONFLICT (id) DO NOTHING
                """;
            cmd.Parameters.Add(new DuckDBParameter("id", id));
            cmd.Parameters.Add(new DuckDBParameter("topic", topic));
            cmd.Parameters.Add(new DuckDBParameter("wa", wa));
            cmd.Parameters.Add(new DuckDBParameter("ra", ra));
            cmd.Parameters.Add(new DuckDBParameter("wb", wb));
            cmd.Parameters.Add(new DuckDBParameter("rb", rb));
            await Task.Run(() => cmd.ExecuteNonQuery(), ct);
        }, ct);
        return id;
    }

    public async Task<IReadOnlyList<(string Topic, string Wing, string Room)>>
        GetTunnelsAsync(string wing, string room, CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT topic,
                   CASE WHEN wing_a = $wing AND room_a = $room THEN wing_b ELSE wing_a END AS other_wing,
                   CASE WHEN wing_a = $wing AND room_a = $room THEN room_b ELSE room_a END AS other_room
            FROM tunnels
            WHERE (wing_a = $wing AND room_a = $room)
               OR (wing_b = $wing AND room_b = $room)
            ORDER BY topic
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        cmd.Parameters.Add(new DuckDBParameter("room", room));

        var list = new List<(string, string, string)>();
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (reader.Read())
            list.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }

    public async Task<IReadOnlyList<(string WingA, string RoomA, string WingB, string RoomB)>>
        GetTunnelsByTopicAsync(string topic, CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT wing_a, room_a, wing_b, room_b
            FROM tunnels
            WHERE topic = $topic
            ORDER BY wing_a, room_a
            """;
        cmd.Parameters.Add(new DuckDBParameter("topic", topic));

        var list = new List<(string, string, string, string)>();
        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (reader.Read())
            list.Add((reader.GetString(0), reader.GetString(1),
                      reader.GetString(2), reader.GetString(3)));
        return list;
    }

    public async Task<IReadOnlyList<TripleSummary>> GetActiveTriplesAsync(
        string? wing, int limit, CancellationToken ct = default)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();

        if (wing is null)
        {
            cmd.CommandText = """
                SELECT subject, predicate, object FROM triples
                WHERE valid_to IS NULL
                ORDER BY valid_from DESC
                LIMIT $limit
                """;
        }
        else
        {
            // Filter by wing via the drawer that sourced each triple.
            // Triples without a drawer_id (drawer_id IS NULL) are excluded when
            // a wing filter is applied — they have no wing association.
            cmd.CommandText = """
                SELECT DISTINCT t.subject, t.predicate, t.object
                FROM triples t
                JOIN drawers d ON d.id = t.drawer_id
                WHERE t.valid_to IS NULL
                  AND d.wing = $wing
                ORDER BY t.valid_from DESC
                LIMIT $limit
                """;
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        }

        cmd.Parameters.Add(new DuckDBParameter("limit", limit));

        var list = new List<TripleSummary>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new TripleSummary(
                reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }

    /// <summary>
    /// F3 fix: active search channel. Given candidate entity names extracted
    /// from a query, return all currently-valid triples involving those entities,
    /// grouped by entity. This lets PalaceSearcher discriminate "A calls B"
    /// from "B calls A" using predicate structure.
    /// </summary>
    public async Task<IReadOnlyList<EntityFacts>> LookupEntitiesForQueryAsync(
        IReadOnlyList<string> entityNames, int limitPerEntity, CancellationToken ct)
    {
        if (entityNames.Count == 0)
            return [];

        var results = new List<EntityFacts>();

        foreach (var name in entityNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entityId = await ResolveEntityIdAsync(name, ct);
            if (entityId is null)
                continue;

            using var ro = dbFactory.OpenReadOnly();
            using var cmd = ro.CreateCommand();
            cmd.CommandText = """
                SELECT subject, predicate, object, confidence
                FROM triples
                WHERE (subject = $eid OR object = $eid)
                  AND valid_to IS NULL
                ORDER BY valid_from DESC
                LIMIT $limit
                """;
            cmd.Parameters.Add(new DuckDBParameter("eid", entityId));
            cmd.Parameters.Add(new DuckDBParameter("limit", limitPerEntity));

            var triples = new List<PredicateTriple>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                triples.Add(new PredicateTriple(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    Convert.ToDouble(reader.GetValue(3))));
            }

            if (triples.Count > 0)
                results.Add(new EntityFacts(name, triples));
        }

        return results;
    }

    /// <summary>
    /// Extract candidate entity names from a text query by matching against
    /// known entities in the knowledge graph. Longest match first.
    /// Server-side filter pushes matching into SQL instead of loading all entities.
    /// </summary>
    public async Task<IReadOnlyList<string>> MatchEntitiesInTextAsync(
        string text, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var lowerTokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length >= 3)
            .Distinct()
            .ToList();
        if (lowerTokens.Count == 0)
            return [];

        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT id, name FROM entities
            WHERE list_has($lowerTokens, lower(name))
               OR list_has_any(
                    list_transform(
                        COALESCE(
                            TRY_CAST(json_extract(properties, '$.aliases') AS VARCHAR[]),
                            []
                        ),
                        x -> lower(x)
                    ),
                    $lowerTokens
                )
            ORDER BY length(name) DESC
            LIMIT $limit
            """;
        // DuckDB expands = ANY(list_param) via UNNEST, which is not allowed in a scalar
        // WHERE position. Use list_has(list, element) for the name check and
        // list_has_any + list_transform for the aliases check instead.
        //   list_has($lowerTokens, lower(name)) -- exact match against token set
        //   json_extract + TRY_CAST -- parse aliases JSON array to VARCHAR[]
        //   COALESCE -- treat missing aliases as empty list
        //   list_transform -- lowercase each alias
        //   list_has_any -- check intersection with $lowerTokens
        cmd.Parameters.Add(new DuckDBParameter("lowerTokens", lowerTokens));
        cmd.Parameters.Add(new DuckDBParameter("limit", limit));

        var matched = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            matched.Add(reader.GetString(1));
        return matched;
    }

    public sealed record EntityFacts(string EntityName, IReadOnlyList<PredicateTriple> Triples);
    public sealed record PredicateTriple(string Subject, string Predicate, string Object, double Confidence);

    public sealed record TripleSummary(string Subject, string Predicate, string Object);

    static string TunnelId(string topic, string wa, string ra, string wb, string rb)
    {
        var input = $"{topic}|{wa}|{ra}|{wb}|{rb}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
                      .ToLowerInvariant()[..16];
    }

    static string EntityId(string name, string type)
    {
        var input = $"{Canonicalize(name)}|{type.ToLowerInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
                      .ToLowerInvariant()[..16];
    }

    static string TripleId(string subject, string predicate, string obj, DateOnly from)
    {
        var input = $"{subject}|{predicate}|{obj}|{from:yyyy-MM-dd}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
                      .ToLowerInvariant()[..16];
    }
}
