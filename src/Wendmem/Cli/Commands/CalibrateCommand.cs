using System.Text.Json;
using System.Text.Json.Nodes;
using DuckDB.NET.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wendmem.Options;
using Wendmem.Services;
using Wendmem.Storage;

namespace Wendmem.Cli.Commands;

internal sealed class CalibrateCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var palaceConfig = services.GetRequiredService<PalaceConfig>();
        var wing = ArgvHelpers.GetWing(args, palaceConfig);

        int samples = ArgvHelpers.GetIntOption(args, "--samples", 200);
        bool writeConfig = ArgvHelpers.HasFlag(args, "--write-config");
        bool dryRun = ArgvHelpers.HasFlag(args, "--dry-run");

        if (dryRun)
            writeConfig = false;

        var factory = services.GetRequiredService<DuckDbConnectionFactory>();
        var embedder = services.GetRequiredService<IEmbedder>();
        var storage = services.GetRequiredService<DrawerStorage>();
        var kg = services.GetRequiredService<KnowledgeGraph>();
        var modelOpts = services.GetRequiredService<IOptions<ModelsOptions>>().Value;

        if (!embedder.IsAvailable)
        {
            Console.Error.WriteLine("Embedding model is not loaded. Calibration requires the ONNX model.");
            return 1;
        }

        var sampled = await SampleDrawersAsync(factory, wing, samples, ct);
        if (sampled.Count < 10)
        {
            Console.Error.WriteLine($"Not enough drawers in wing '{wing}' ({sampled.Count}). Need at least 10.");
            return 1;
        }

        int halfSamples = Math.Min(sampled.Count, samples / 2);
        var positiveDrawers = sampled.Take(halfSamples).ToList();
        var negativeDrawers = sampled.Skip(halfSamples).ToList();

        var pairs = new List<CalibPair>();
        foreach (var d in positiveDrawers)
        {
            var query = ExtractNounPhrase(d.Content);
            pairs.Add(new CalibPair(query, d.Id, Relevant: true));
        }

        var rooms = positiveDrawers.Select(d => d.Room).Distinct().ToList();
        int negIdx = 0;
        foreach (var d in positiveDrawers)
        {
            if (negIdx >= negativeDrawers.Count)
                break;
            var negDrawer = negativeDrawers[negIdx++];
            var negQuery = ExtractNounPhrase(negDrawer.Content);
            pairs.Add(new CalibPair(negQuery, d.Id, Relevant: false));
        }

        var evalResults = new List<CalibResult>();
        foreach (var pair in pairs.Take(samples))
        {
            var vec = await embedder.EmbedQueryAsync(pair.Query, ct);
            var searchHits = await storage.HybridSearchAsync(pair.Query, vec, wing, null, k: 10, ct, 0.5f, true);

            var topHit = searchHits.FirstOrDefault();
            float score = topHit?.Score ?? 0f;
            bool hitRelevant = topHit is not null && topHit.Drawer.Id == pair.DrawerId;

            bool bm25Active = false;
            if (topHit is not null)
            {
                var bm25Results = await storage.FtsSearchAsync(pair.Query, wing, limit: 50, ct);
                bm25Active = bm25Results.Any(r => r.Drawer.Id == topHit.Drawer.Id);
            }

            var t = palaceConfig.GetThresholds(wing);
            bool semanticActive = score > t.Medium;
            var kgEntities = await kg.MatchEntitiesInTextAsync(pair.Query, limit: 5, ct);
            bool kgActive = kgEntities.Count > 0;

            evalResults.Add(new CalibResult(
                score, bm25Active, semanticActive, kgActive, pair.Relevant, hitRelevant));
        }

        var ece = ComputeEce(evalResults);
        var brier = ComputeBrier(evalResults);
        var recommended = RecommendThresholds(evalResults);
        var current = palaceConfig.GetThresholds(wing);

        int full = 0, partial = 0, single = 0;
        foreach (var r in evalResults)
        {
            int active = (r.Bm25Active ? 1 : 0) + (r.SemanticActive ? 1 : 0) + (r.KgActive ? 1 : 0);
            switch (active)
            {
                case 3:
                    full++;
                    break;
                case 2:
                    partial++;
                    break;
                default:
                    single++;
                    break;
            }
        }
        int totalPairs = evalResults.Count;
        float fullPct = totalPairs > 0 ? 100f * full / totalPairs : 0;
        float partialPct = totalPairs > 0 ? 100f * partial / totalPairs : 0;
        float singlePct = totalPairs > 0 ? 100f * single / totalPairs : 0;

        Console.Out.WriteLine("WendMem Calibration Report");
        Console.Out.WriteLine("══════════════════════════════════════════════");
        Console.Out.WriteLine($"Wing          : {wing}");
        Console.Out.WriteLine($"Samples       : {evalResults.Count} ({positiveDrawers.Count} positive / {evalResults.Count - positiveDrawers.Count} negative)");
        Console.Out.WriteLine($"Embedding     : {modelOpts.EmbeddingModel.OnnxPath}");
        Console.Out.WriteLine();
        Console.Out.WriteLine($"{"Current Thresholds",-26}{"Recommended Thresholds"}");
        Console.Out.WriteLine($"{"──────────────────────────",-26}{"──────────────────────────"}");
        Console.Out.WriteLine($"{"high",-15}{current.High,0:F2}   {"high",-15}{recommended.High:F2}");
        Console.Out.WriteLine($"{"medium",-15}{current.Medium,0:F2}   {"medium",-15}{recommended.Medium:F2}");
        Console.Out.WriteLine($"{"can_proceed",-15}{current.CanProceedMin,0:F2}   {"can_proceed",-15}{recommended.CanProceedMin:F2}");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Calibration Metrics");
        Console.Out.WriteLine("───────────────────");
        string eceStatus = ece < 0.05 ? "✓ acceptable" : "⚠ needs calibration";
        string brierStatus = brier < 0.15 ? "✓ acceptable" : "⚠ needs calibration";
        Console.Out.WriteLine($"ECE           : {ece:F3}  (target < 0.05)  {eceStatus}");
        Console.Out.WriteLine($"Brier Score   : {brier:F3}  (target < 0.15)  {brierStatus}");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Agreement Distribution");
        Console.Out.WriteLine("──────────────────────");
        Console.Out.WriteLine($"full          : {fullPct:F0}%");
        Console.Out.WriteLine($"partial       : {partialPct:F0}%");
        Console.Out.WriteLine($"single        : {singlePct:F0}%");
        Console.Out.WriteLine();

        if (writeConfig)
        {
            await WriteConfigAsync(wing, recommended);
            Console.Out.WriteLine($"Recommendation: thresholds written to palace-config.json under wing_overrides.{wing}.");
        }
        else
        {
            Console.Out.WriteLine("Recommendation: thresholds adjusted for this corpus.");
            Console.Out.WriteLine("Run with --write-config to apply.");
        }

        Console.Out.WriteLine("══════════════════════════════════════════════");
        return 0;
    }

    record CalibPair(string Query, string DrawerId, bool Relevant);
    record CalibResult(float Score, bool Bm25Active, bool SemanticActive, bool KgActive, bool QueryRelevant, bool HitRelevant);

    static async Task<List<(string Id, string Room, string Content)>> SampleDrawersAsync(
        DuckDbConnectionFactory factory, string wing, int samples, CancellationToken ct)
    {
        var results = new List<(string Id, string Room, string Content)>();
        await using var ro = factory.OpenReadOnly();
        await using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT id, room, content FROM drawers
            WHERE wing = $wing AND valid_to IS NULL AND is_representative
            ORDER BY RANDOM()
            LIMIT $limit
            """;
        cmd.Parameters.Add(new DuckDBParameter("wing", wing));
        cmd.Parameters.Add(new DuckDBParameter("limit", samples * 2));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return results;
    }

    static string ExtractNounPhrase(string content)
    {
        var span = content.AsSpan();
        int end = span.Length;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == '.' || span[i] == '\n' || span[i] == '\r')
            {
                end = i;
                break;
            }
        }
        var phrase = span[..Math.Min(end, 80)].Trim();
        return phrase.Length > 10 ? phrase.ToString() : content[..Math.Min(content.Length, 80)];
    }

    static float ComputeEce(List<CalibResult> results)
    {
        if (results.Count == 0)
            return 0f;
        int numBins = 10;
        var bins = new (float SumScore, int Count, int Relevant)[numBins];

        foreach (var r in results)
        {
            int bin = Math.Min((int)(r.Score * numBins), numBins - 1);
            bins[bin].SumScore += r.Score;
            bins[bin].Count++;
            if (r.QueryRelevant)
                bins[bin].Relevant++;
        }

        float ece = 0f;
        foreach (var b in bins)
        {
            if (b.Count == 0)
                continue;
            float avgScore = b.SumScore / b.Count;
            float fracRelevant = (float)b.Relevant / b.Count;
            ece += Math.Abs(avgScore - fracRelevant) * b.Count / results.Count;
        }
        return ece;
    }

    static float ComputeBrier(List<CalibResult> results)
    {
        if (results.Count == 0)
            return 0f;
        float sum = 0f;
        foreach (var r in results)
        {
            float label = r.QueryRelevant ? 1f : 0f;
            float diff = r.Score - label;
            sum += diff * diff;
        }
        return sum / results.Count;
    }

    static ConfidenceThresholds RecommendThresholds(List<CalibResult> results)
    {
        if (results.Count == 0)
            return new ConfidenceThresholds();

        var sorted = results.OrderByDescending(r => r.Score).ToList();
        float high = FindPrecisionThreshold(sorted, 0.90f);
        float medium = FindPrecisionThreshold(sorted, 0.70f);
        float canProceed = FindPrecisionThreshold(sorted, 0.50f);

        return new ConfidenceThresholds(high, medium, canProceed);
    }

    static float FindPrecisionThreshold(List<CalibResult> sorted, float targetPrecision)
    {
        int relevantSoFar = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            if (sorted[i].QueryRelevant)
                relevantSoFar++;
            float precision = (float)relevantSoFar / (i + 1);
            if (precision >= targetPrecision && i > 0)
            {
                return sorted[i].Score;
            }
        }
        return sorted.Count > 0 ? sorted[^1].Score : 0.40f;
    }

    static async Task WriteConfigAsync(string wing, ConfidenceThresholds recommended)
    {
        var configPath = Path.Combine(Directory.GetCurrentDirectory(), "palace-config.json");
        JsonNode? root;

        if (File.Exists(configPath))
        {
            var existing = await File.ReadAllTextAsync(configPath);
            root = JsonNode.Parse(existing);
        }
        else
        {
            root = new JsonObject();
        }

        var overrides = root!["wing_overrides"] as JsonObject ?? new JsonObject();
        if (root!["wing_overrides"] is null)
            root["wing_overrides"] = overrides;

        var wingObj = new JsonObject
        {
            ["confidence"] = new JsonObject
            {
                ["thresholds"] = new JsonObject
                {
                    ["high"] = recommended.High,
                    ["medium"] = recommended.Medium,
                    ["can_proceed_min"] = recommended.CanProceedMin
                }
            }
        };

        overrides[wing] = wingObj;

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(configPath, json);

        Console.Out.WriteLine($"Config updated: wing_overrides.{wing}.confidence.thresholds");
    }
}
