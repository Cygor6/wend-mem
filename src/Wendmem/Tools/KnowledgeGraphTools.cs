using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Wendmem.Models;
using Wendmem.Serialization;
using Wendmem.Services;
using Wendmem.Storage;

namespace Wendmem.Tools;

[McpServerToolType]
sealed class KnowledgeGraphTools
{
    [McpServerTool(Name = "AddTriple"), Description(
        "Record a temporal fact as a knowledge-graph triple (subject — predicate — object), " +
        "valid from a date until invalidated. Use when the user states a relationship that " +
        "should persist across sessions: 'I work at Acme', 'this project uses DuckDB', " +
        "'wendmem depends on duckdb-net'. Missing entities are created automatically. " +
        "To retire a fact later, use InvalidateTriple.")]
    static async Task<string> AddTriple(
        KnowledgeGraph kg,
        WalLogger wal,
        ILogger<KnowledgeGraphTools> logger,
        [Description("Subject entity name in lowercase (e.g. 'user', 'wendmem', 'alice'). " +
                     "The entity is created automatically if it doesn't exist.")]
        string subject,
        [Description("Relation name in snake_case. Common predicates: works_at, uses, depends_on, " +
                     "lives_in, child_of, owns. Pick a stable name — consistency matters across triples.")]
        string predicate,
        [Description("Object entity name (lowercase) or literal value. " +
                     "Same auto-create behavior as subject.")]
        string obj,
        [Description("Optional ISO date the fact became true (YYYY-MM-DD). Defaults to today.")]
        string? validFrom = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation("AddTriple subject={Subject} predicate={Predicate} object={Object}", subject, predicate, obj);
        DateOnly? from = validFrom is not null ? DateOnly.Parse(validFrom) : null;

        wal.Log("kg_add", new Dictionary<string, string?>
        {
            ["subject"] = subject,
            ["predicate"] = predicate,
            ["object"] = obj
        });

        var (id, conflict) = await kg.AddTripleAsync(subject, predicate, obj, from, ct: ct);

        logger.LogInformation("AddTriple → {Subject} {Predicate} {Object}", subject, predicate, obj);

        var decisionSupport = new McpDecisionSupport(
            CanProceed: true,
            SuggestedAction: conflict == null ? SuggestedAction.Proceed : SuggestedAction.Verify,
            Summary: conflict == null
                ? $"Triple '{subject} {predicate} {obj}' recorded."
                : $"Triple recorded, but conflict detected: {conflict.Message}"
        );

        return JsonSerializer.Serialize(
            McpResponse.Ok("AddTriple", new AddTripleResult(id, subject, predicate, obj, conflict), sw.ElapsedMilliseconds, decisionSupport: decisionSupport),
            WendmemJsonContext.Default.McpResponseAddTripleResult);
    }

    [McpServerTool(Name = "InvalidateTriple"), Description(
        "Mark a knowledge-graph fact as no longer true by setting its valid_to to today. " +
        "Use when you learn that a previously-true relationship has changed — the user changed " +
        "jobs, a tool was deprecated, a project was renamed. The triple stays in history " +
        "(it WAS true) but stops appearing in WakeUp's active facts. " +
        "Pair with AddTriple to record what is now true.")]
    static async Task<string> InvalidateTriple(
        KnowledgeGraph kg,
        WalLogger wal,
        ILogger<KnowledgeGraphTools> logger,
        [Description("Subject entity name (must match the existing triple).")]
        string subject,
        [Description("Relation name (must match the existing triple).")]
        string predicate,
        [Description("Object entity name or literal value (must match the existing triple).")]
        string obj,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation("InvalidateTriple subject={Subject} predicate={Predicate} object={Object}", subject, predicate, obj);

        wal.Log("kg_invalidate", new Dictionary<string, string?>
        {
            ["subject"] = subject,
            ["predicate"] = predicate,
            ["object"] = obj
        });

        await kg.InvalidateAsync(subject, predicate, obj, ct: ct);

        logger.LogInformation("InvalidateTriple → retired");

        var decisionSupport = new McpDecisionSupport(
            CanProceed: true,
            SuggestedAction: SuggestedAction.Proceed,
            Summary: $"Triple '{subject} {predicate} {obj}' invalidated."
        );

        return JsonSerializer.Serialize(
            McpResponse.Ok("InvalidateTriple", new InvalidateResult(true, subject, predicate, obj), sw.ElapsedMilliseconds, decisionSupport: decisionSupport),
            WendmemJsonContext.Default.McpResponseInvalidateResult);
    }
}
