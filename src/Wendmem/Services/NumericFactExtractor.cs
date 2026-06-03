using System.Text.RegularExpressions;
using Wendmem.Storage;

namespace Wendmem.Services;

/// <summary>
/// Extracts structured numeric facts from drawer content
/// and stores them as KG triples so that numeric-swapped pairs
/// ("takes 3 args" vs "takes 5 args") become distinguishable.
/// Never throws — failures are silently swallowed to avoid blocking ingestion.
/// </summary>
sealed partial class NumericFactExtractor(KnowledgeGraph kg)
{
    // "Parse() takes 3"
    [GeneratedRegex(@"(\w+)\s*\([^)]*\)\s+takes?\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ParamCountPattern();

    // "DuckDB v1.5.3"
    [GeneratedRegex(@"([\w.]+)\s+v?(\d+\.\d[\d.]*)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();

    // "returns Task<string>"
    [GeneratedRegex(@"\breturns?\s+([\w<>\[\]]+)")]
    private static partial Regex ReturnTypePattern();

    // "timeout: 30s"
    [GeneratedRegex(@"\btimeout[:\s]+(\d+)\s*(ms|s|min|sec)", RegexOptions.IgnoreCase)]
    private static partial Regex TimeoutPattern();

    // "port: 5432"
    [GeneratedRegex(@"\bport[:\s]+(\d{2,5})\b", RegexOptions.IgnoreCase)]
    private static partial Regex PortPattern();

    // "chunk_size: 800"
    [GeneratedRegex(@"chunk[_\s]size[:\s=]+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ChunkSizePattern();

    public async Task ExtractAsync(string content, string room, string? source, DateTimeOffset minedAt, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(content))
                return;

            var triples = ExtractAll(content, room);

            foreach (var (subject, predicate, obj) in triples)
            {
                ct.ThrowIfCancellationRequested();
                await kg.AddTripleAsync(
                    subject, predicate, obj,
                    sourceRoom: room,
                    sourceFile: source,
                    confidence: 0.9,
                    ct: ct).ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }

    private static List<(string subject, string predicate, string obj)> ExtractAll(string content, string room)
    {
        var triples = new List<(string, string, string)>();

        foreach (Match m in ParamCountPattern().Matches(content))
        {
            triples.Add((m.Groups[1].Value, "has_param_count", m.Groups[2].Value));
        }

        foreach (Match m in VersionPattern().Matches(content))
        {
            triples.Add((m.Groups[1].Value, "has_version", m.Groups[2].Value));
        }

        foreach (Match m in ReturnTypePattern().Matches(content))
        {
            var subject = ExtractNearbyIdentifier(content, m.Index) ?? "unknown";
            triples.Add((subject, "returns_type", m.Groups[1].Value));
        }

        foreach (Match m in TimeoutPattern().Matches(content))
        {
            var subject = ExtractNearbyIdentifier(content, m.Index) ?? room;
            triples.Add((subject, "has_timeout", $"{m.Groups[1].Value}{m.Groups[2].Value}"));
        }

        foreach (Match m in PortPattern().Matches(content))
        {
            var subject = ExtractNearbyIdentifier(content, m.Index) ?? room;
            triples.Add((subject, "uses_port", m.Groups[1].Value));
        }

        foreach (Match m in ChunkSizePattern().Matches(content))
        {
            var subject = ExtractNearbyIdentifier(content, m.Index) ?? room;
            triples.Add((subject, "has_value", m.Groups[1].Value));
        }

        return triples;
    }

    /// <summary>
    /// Looks backwards from the match position for a nearby identifier-like token
    /// to use as the triple subject.
    /// </summary>
    private static string? ExtractNearbyIdentifier(string content, int matchIndex)
    {
        var start = Math.Max(0, matchIndex - 80);
        var preceding = content[start..matchIndex];

        var idMatch = Regex.Match(preceding, @"[\w.]+(?=\s*$)");
        if (idMatch.Success && idMatch.Value.Length >= 2)
            return idMatch.Value;

        idMatch = Regex.Match(preceding, @"(\w[\w.]*)\s*\(");
        if (idMatch.Success)
            return idMatch.Groups[1].Value;

        return null;
    }
}
