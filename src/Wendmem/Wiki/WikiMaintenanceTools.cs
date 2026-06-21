using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Wendmem.Models;
using Wendmem.Services;
using Wendmem.Wiki.Json;
using Wendmem.Wiki.Models;

namespace Wendmem.Wiki;

[McpServerToolType]
sealed class WikiMaintenanceTools
{

    [McpServerTool(Name = "ListPendingUpdates"), Description("List wiki pages with new drawer evidence queued for review.")]
    static async Task<string> ListPendingUpdates(
        PendingUpdateService svc,
        PalaceConfig config,
        ILogger<WikiMaintenanceTools> logger,
        [Description("Wing to scope to (optional — omit to use the configured default wing).")] string? wing = null,
        [Description("Specific page path (optional).")] string? pagePath = null,
        [Description("Max results. Default 50.")] int limit = 50,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        wing = PathValidator.ResolveWing(wing, config);

        logger.LogInformation("ListPendingUpdates wing={Wing}", wing);
        var updates = await svc.ListPendingAsync(wing, pagePath, limit, ct);
        logger.LogInformation("ListPendingUpdates → {Count} pending", updates.Count);
        var dtos = updates.Select(u => new PendingUpdateDto(
            u.PagePath, u.DrawerId, u.Similarity,
            u.QueuedAt.ToUnixTimeSeconds())).ToList();

        var result = new PendingUpdatesResponseDto(dtos);

        var decisionSupport = new McpDecisionSupport(
            CanProceed: true,
            SuggestedAction: SuggestedAction.Proceed,
            Summary: $"Found {dtos.Count} pending wiki updates."
        );

        return JsonSerializer.Serialize(
            McpResponse.Ok("ListPendingUpdates", result, sw.ElapsedMilliseconds, decisionSupport: decisionSupport),
            WendmemWikiJsonContext.Default.McpResponsePendingUpdatesResponseDto);
    }

    [McpServerTool(Name = "DismissPendingUpdate"), Description("Dismiss a pending wiki update without applying it.")]
    static async Task<string> DismissPendingUpdate(
        PendingUpdateService svc,
        [Description("Page path of the pending update.")] string pagePath,
        [Description("Drawer ID of the pending update.")] string drawerId,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        pagePath = PathValidator.ValidatePath(pagePath);
        drawerId = PathValidator.ValidateDrawerId(drawerId);

        await svc.ResolveAsync(pagePath, drawerId, "dismissed", ct);

        var result = new DismissResponseDto(true, pagePath, drawerId);

        var decisionSupport = new McpDecisionSupport(
            CanProceed: true,
            SuggestedAction: SuggestedAction.Proceed,
            Summary: $"Pending update for '{pagePath}' (drawer {drawerId}) dismissed."
        );

        return JsonSerializer.Serialize(
            McpResponse.Ok("DismissPendingUpdate", result, sw.ElapsedMilliseconds, decisionSupport: decisionSupport),
            WendmemWikiJsonContext.Default.McpResponseDismissResponseDto);
    }

    [McpServerTool(Name = "LintWiki"), Description("Lint the wiki and return a structured action list with findings.")]
    static async Task<string> LintWiki(
        WikiLinter linter,
        PalaceConfig config,
        ILogger<WikiMaintenanceTools> logger,
        [Description("Wing (optional — omit to use the configured default wing).")] string? wing = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        wing = PathValidator.ResolveWing(wing, config);

        logger.LogInformation("LintWiki wing={Wing}", wing ?? "*");
        var report = await linter.LintAsync(wing, ct);
        var errors = report.Findings.Count(f => f.Severity == "error");
        var warns = report.Findings.Count(f => f.Severity == "warning");
        logger.LogInformation("LintWiki → {Errors} errors  {Warns} warns", errors, warns);
        var findingDtos = report.Findings.Select(f => new LintFindingDto(
            f.Rule, f.Severity, f.PagePath, f.Message,
            f.Details)).ToList();

        var result = new LintWikiResponseDto(report.Wing, report.PageCount,
                findingDtos, report.CountsByRule);

        var decisionSupport = new McpDecisionSupport(
            CanProceed: errors == 0,
            SuggestedAction: errors == 0 ? SuggestedAction.Proceed : SuggestedAction.Verify,
            Summary: $"Wiki linting completed: {errors} errors, {warns} warnings across {report.PageCount} pages."
        );

        return JsonSerializer.Serialize(
            McpResponse.Ok("LintWiki", result, sw.ElapsedMilliseconds, decisionSupport: decisionSupport),
            WendmemWikiJsonContext.Default.McpResponseLintWikiResponseDto);
    }

    [McpServerTool(Name = "Distill"), Description(
        "Session boundary event — call before ending every non-trivial session. " +
        "Crystallizes this session's drawer evidence into persistent wiki pages. " +
        "Returns candidate existing pages and a draft scaffold; then call WikiWrite. " +
        "Skipping this means session knowledge stays in raw drawers and is never synthesized. " +
        "Treat this as mandatory housekeeping, not optional filing.")]
    static async Task<string> Distill(
        PalaceSearcher searcher,
        PendingUpdateService pending,
        ActivityLog activityLog,
        PalaceConfig config,
        ILogger<WikiMaintenanceTools> logger,
        [Description("One-paragraph summary of what was decided, built, or learned this session. " +
                     "Write it as if briefing your future self at the start of the next session.")] string sessionSummary,
        [Description("Wing (optional — omit to use the configured default wing).")] string? wing = null,
        [Description("Hint at relevant page paths (optional, comma-separated).")] string? pageHints = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        wing = PathValidator.ResolveWing(wing, config);

        logger.LogInformation("Distill wing={Wing}", wing);

        // Log the distill call
        await activityLog.LogAsync("distill", wing, null, null, sessionSummary, ct);

        // Search for candidate existing pages
        var results = await searcher.SearchMemoriesAsync(sessionSummary, wing, null, 5, ct);
        var candidatePages = new List<DistillCandidatePageDto>();

        logger.LogInformation("Distill → {Count} candidates", results.Count);

        // Include hinted pages first
        if (!string.IsNullOrWhiteSpace(pageHints))
        {
            var hints = pageHints.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var hint in hints)
            {
                var pendingForPage = await pending.ListPendingAsync(wing, hint, limit: 10, ct);
                candidatePages.Add(new DistillCandidatePageDto(
                    hint, pendingForPage.Select(p => p.DrawerId).ToList(),
                    null, null));
            }
        }

        // Add search-derived candidates
        foreach (var r in results.Take(5))
        {
            candidatePages.Add(new DistillCandidatePageDto(
                null, null, r.Drawer.Id, r.Score));
        }

        // Generate suggested path from session summary
        var suggestedPath = ToKebabPath(sessionSummary);
        var suggestedTitle = char.ToUpper(sessionSummary[0]) +
            (sessionSummary.Length > 1 ? sessionSummary[1..Math.Min(60, sessionSummary.Length)].TrimEnd() : "");

        var response = new DistillResponseDto(
            wing,
            candidatePages,
            new DistillScaffoldDto(suggestedPath, suggestedTitle,
                "## Overview\n\n<TODO>\n\n## Details\n\n<TODO>"),
            "Call WikiWrite with either a candidate path (update) or the suggested path (create), citing the source drawer IDs.");

        var decisionSupport = new McpDecisionSupport(
            CanProceed: true,
            SuggestedAction: SuggestedAction.Verify,
            Summary: "Distillation successful. Review candidates and suggested scaffold before calling WikiWrite."
        );

        return JsonSerializer.Serialize(
            McpResponse.Ok("Distill", response, sw.ElapsedMilliseconds, decisionSupport: decisionSupport),
            WendmemWikiJsonContext.Default.McpResponseDistillResponseDto);
    }

    static string ToKebabPath(string text)
    {
        // Take first ~5 meaningful words, strip non-ASCII, and kebab-case them
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .Take(5)
            .Select(w => w.ToLowerInvariant().TrimEnd('.', ',', ':', ';'))
            .Select(w => new string(w.Where(char.IsAsciiLetterOrDigit).ToArray()))
            .Where(w => w.Length > 2);
        return string.Join("-", words);
    }
}
