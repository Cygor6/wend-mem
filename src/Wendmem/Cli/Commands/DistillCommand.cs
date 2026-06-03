using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wendmem.Services;
using Wendmem.Wiki;
using Wendmem.Wiki.Json;
using Wendmem.Wiki.Models;

namespace Wendmem.Cli.Commands;

internal sealed class DistillCommand
{
    public async Task<int> RunAsync(
        string[] args, IServiceProvider services, CancellationToken ct)
    {
        var wing = ArgvHelpers.GetOption(args, "--wing");
        if (wing is null)
        {
            Console.Error.WriteLine("Usage: wendmem distill --wing W --summary <text> [--hints <paths>]");
            return 1;
        }

        var summary = ArgvHelpers.GetOption(args, "--summary");
        if (summary is null)
        {
            Console.Error.WriteLine("Error: --summary is required.");
            return 1;
        }

        var hints = ArgvHelpers.GetOption(args, "--hints");

        var searcher = services.GetRequiredService<PalaceSearcher>();
        var pending = services.GetRequiredService<PendingUpdateService>();
        var log = services.GetRequiredService<ActivityLog>();

        await log.LogAsync("distill", wing, null, null, summary, ct);

        var results = await searcher.SearchMemoriesAsync(summary, wing, null, 5, ct);
        var candidatePages = new List<DistillCandidatePageDto>();

        if (!string.IsNullOrWhiteSpace(hints))
        {
            var hintList = hints.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var hint in hintList)
            {
                var pendingForPage = await pending.ListPendingAsync(wing, hint, limit: 10, ct);
                candidatePages.Add(new DistillCandidatePageDto(
                    hint, pendingForPage.Select(p => p.DrawerId).ToList(), null, null));
            }
        }

        foreach (var r in results.Take(5))
        {
            candidatePages.Add(new DistillCandidatePageDto(null, null, r.Drawer.Id, r.Score));
        }

        var suggestedPath = ToKebabPath(summary);
        var suggestedTitle = char.ToUpper(summary[0]) +
            (summary.Length > 1 ? summary[1..Math.Min(60, summary.Length)].TrimEnd() : "");

        var response = new DistillResponseDto(
            wing,
            candidatePages,
            new DistillScaffoldDto(suggestedPath, suggestedTitle,
                "## Overview\n\n<TODO>\n\n## Details\n\n<TODO>"),
            "Call WikiWrite with either a candidate path (update) or the suggested path (create), citing the source drawer IDs.");

        Console.Out.WriteLine(JsonSerializer.Serialize(response, WendmemWikiJsonContext.Default.DistillResponseDto));
        return 0;
    }

    static string ToKebabPath(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .Take(5)
            .Select(w => w.ToLowerInvariant().TrimEnd('.', ',', ':', ';'))
            .Where(w => w.Length > 0);
        return string.Join("-", words);
    }
}
