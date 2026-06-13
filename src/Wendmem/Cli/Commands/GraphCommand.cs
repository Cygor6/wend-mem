using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wendmem.Services;

namespace Wendmem.Cli.Commands;

public sealed record GraphOptions(
    string Wing,
    string Output,
    int Limit,
    bool NoDrawers,
    bool NoTriples,
    bool NoEpisodes,
    bool NoSkills);

public sealed record GraphNode(
    string Id,
    string Type,
    string Label,
    string? Wing,
    string? Room,
    string? Source,
    string? Snippet,
    string? Title,
    string? Desc,
    string? Subject,
    string? Predicate,
    string? Object,
    string? ValidFrom,
    string? Outcome,
    string? NextTime,
    double? SuccessRate);

public sealed record GraphLink(
    string Source,
    string Target,
    string Type);

public sealed record GraphData(
    List<GraphNode> Nodes,
    List<GraphLink> Links,
    string Wing,
    DateTimeOffset GeneratedAt);

internal static class GraphCommand
{
    internal static async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wing = ArgvHelpers.GetOption(args, "--wing");
        if (wing is null)
        {
            Console.Error.WriteLine("Usage: wendmem graph --wing <wing> [--output <path>] [--limit N] [--no-drawers] [--no-triples] [--no-episodes] [--no-skills]");
            return 1;
        }

        var opts = new GraphOptions(
            Wing: wing,
            Output: ArgvHelpers.GetOption(args, "--output") ?? "graph.html",
            Limit: ArgvHelpers.GetIntOption(args, "--limit", 150),
            NoDrawers: ArgvHelpers.HasFlag(args, "--no-drawers"),
            NoTriples: ArgvHelpers.HasFlag(args, "--no-triples"),
            NoEpisodes: ArgvHelpers.HasFlag(args, "--no-episodes"),
            NoSkills: ArgvHelpers.HasFlag(args, "--no-skills"));

        var svc = services.GetRequiredService<GraphDataService>();

        Console.Out.WriteLine($"Building graph for wing '{wing}'…");

        var data = await svc.BuildAsync(opts, ct);
        var html = GraphHtmlRenderer.Render(data);

        var outPath = opts.Output is "graph.html"
            ? $"graph-{wing}.html"
            : opts.Output;

        await File.WriteAllTextAsync(outPath, html, Encoding.UTF8, ct);

        Console.Out.WriteLine(
            $"✓ Written {outPath} " +
            $"({data.Nodes.Count} nodes, {data.Links.Count} edges)");
        return 0;
    }
}
