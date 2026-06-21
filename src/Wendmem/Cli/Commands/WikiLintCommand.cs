using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wendmem.Wiki;
using Wendmem.Wiki.Json;
using Wendmem.Wiki.Models;

namespace Wendmem.Cli.Commands;

internal sealed class WikiLintCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var config = services.GetRequiredService<PalaceConfig>();
        var wing = ArgvHelpers.GetWing(args, config);
        var json = args.Contains("--json");

        var linter = services.GetRequiredService<WikiLinter>();
        var report = await linter.LintAsync(wing, ct);

        if (json)
        {
            var findingDtos = report.Findings.Select(f => new LintFindingDto(
                f.Rule, f.Severity, f.PagePath, f.Message, f.Details)).ToList();
            var dto = new LintWikiResponseDto(
                report.Wing, report.PageCount, findingDtos, report.CountsByRule);
            Console.Out.WriteLine(JsonSerializer.Serialize(
                dto, WendmemWikiJsonContext.Default.LintWikiResponseDto));
            return 0;
        }

        if (report.Findings.Count == 0)
        {
            Console.Out.WriteLine($"Wiki is healthy - no issues found across {report.PageCount} pages.");
            return 0;
        }

        foreach (var group in report.Findings.GroupBy(f => f.Rule))
        {
            var severity = group.First().Severity;
            var label = severity switch
            {
                "error" => "ERROR",
                "warn" => "WARN",
                _ => "INFO"
            };
            Console.Out.WriteLine($"[{label}] {group.Key} ({group.Count()} findings):");
            foreach (var f in group)
            {
                var path = string.IsNullOrEmpty(f.PagePath) ? "(no page)" : f.PagePath;
                Console.Out.WriteLine($"  {path}: {f.Message}");
            }
            Console.Out.WriteLine();
        }

        Console.Out.WriteLine($"Total: {report.Findings.Count} findings across {report.PageCount} pages.");
        return 0;
    }
}
