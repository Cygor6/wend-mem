using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wendmem.Serialization;
using Wendmem.Services.Okf;

namespace Wendmem.Cli.Commands;

internal sealed class OkfCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        if (args.Length == 0)
            return PrintHelp();

        return args[0] switch
        {
            "import" => await ImportAsync(args[1..], services, ct),
            _ => Unknown(args[0]),
        };
    }

    static async Task<int> ImportAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        var bundle = ArgvHelpers.GetPositional(args, 0);
        if (bundle is null)
        {
            Console.Error.WriteLine("Usage: wendmem okf import <bundle> [--wing W] [--room <r>] [--dry-run] [--json]");
            return 1;
        }

        if (!Directory.Exists(bundle))
        {
            Console.Error.WriteLine($"Error: bundle directory not found: {bundle}");
            return 1;
        }

        var config = services.GetRequiredService<PalaceConfig>();
        var wing = ArgvHelpers.GetWing(args, config);
        var room = ArgvHelpers.GetOption(args, "--room");
        var dryRun = ArgvHelpers.HasFlag(args, "--dry-run");
        var json = ArgvHelpers.HasFlag(args, "--json");

        var importer = services.GetRequiredService<OkfImporter>();
        var report = await importer.ImportAsync(bundle, wing, room, dryRun, ct);

        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(
                report, WendmemJsonContext.Default.OkfImportReport));
            return 0;
        }

        PrintHumanReport(report);
        return 0;
    }

    static void PrintHumanReport(OkfImportReport r)
    {
        var mode = r.DryRun ? "DRY-RUN plan" : "import";
        Console.Out.WriteLine($"OKF {mode} — wing '{r.Wing}', room '{r.Room}'");
        Console.Out.WriteLine($"Bundle: {r.BundleRoot}");
        Console.Out.WriteLine(
            $"Concepts: {r.ConceptsFound} found | {r.ConceptsImported} imported | {r.ConceptsSkipped} skipped");
        if (!r.DryRun)
            Console.Out.WriteLine($"Drawers added: {r.TotalDrawers} | KG triples: {r.TotalTriples}");
        Console.Out.WriteLine();

        foreach (var c in r.Concepts)
        {
            if (c.Skipped)
            {
                Console.Out.WriteLine($"  SKIP  {c.ConceptId} — {c.SkipReason}");
                continue;
            }

            var label = r.DryRun ? "PLAN " : "OK   ";
            Console.Out.WriteLine(
                $"  {label} {c.ConceptId} — type:{c.Type} | drawers:{c.DrawerCount} triples:{c.TripleCount} links:{c.RewrittenLinks}");
        }
    }

    static int PrintHelp()
    {
        Console.Out.WriteLine("""
            Usage:
              wendmem okf import <bundle> [--wing W] [--room <r>] [--dry-run] [--json]

            Imports an OKF v0.1 bundle (a directory of concept markdown files) into
            wendmem: body → source drawers, frontmatter → KG triples, body → wiki
            pages with cross-links rewritten to [[wikilinks]]. Reserved index.md /
            log.md and non-conformant files are skipped with a reason.
            """);
        return 0;
    }

    static int Unknown(string sub)
    {
        Console.Error.WriteLine($"Unknown okf subcommand: '{sub}'. Available: import");
        return 2;
    }
}
