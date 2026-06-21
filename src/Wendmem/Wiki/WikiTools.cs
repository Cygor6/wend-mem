using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Wendmem.Models;
using Wendmem.Wiki.Json;
using Wendmem.Wiki.Models;

namespace Wendmem.Wiki;

[McpServerToolType]
public class WikiTools
{
    private readonly PalaceConfig _config;

    public WikiTools(PalaceConfig config)
    {
        _config = config;
    }

    [McpServerTool(Name = "WikiRead"), Description(
        "Read a wiki synthesis page by path. Wiki pages are LLM-maintained syntheses of " +
        "multiple drawers, with citations to source drawers as provenance. Returns markdown " +
        "content plus the citation list. Use after seeing a path in WakeUp's index, or to " +
        "follow a [[wikilink]] from another page.")]
    public async Task<string> WikiRead(
        WikiStorage wiki,
        ILogger<WikiTools> logger,
        [Description("Page path: kebab-case ASCII, optional '/' for hierarchy. " +
                     "Examples: 'wakeup-design', 'architecture/storage', 'user-prefs/style'.")]
        string path,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation("WikiRead path={Path}", path);
        var page = await wiki.ReadAsync(path, ct);
        if (page is null)
        {
            return JsonSerializer.Serialize(
                McpResponse.Fail<WikiPageDto>("WikiRead", "not_found", $"Wiki page not found: {path}"),
                WendmemWikiJsonContext.Default.McpResponseWikiPageDto);
        }

        var wordCount = page.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        logger.LogInformation("WikiRead → {WordCount} words", wordCount);

        var dto = new WikiPageDto(
            page.Path, page.Wing, page.Title, page.Content,
            page.Citations, page.Backlinks,
            page.UpdatedAt.ToUnixTimeSeconds(),
            page.UpdatedBy);

        var decisionSupport = new McpDecisionSupport(
            CanProceed: true,
            SuggestedAction: SuggestedAction.Proceed,
            Summary: $"Read wiki page '{path}'. {wordCount} words, {page.Citations.Count} citations."
        );

        return JsonSerializer.Serialize(
            McpResponse.Ok("WikiRead", dto, sw.ElapsedMilliseconds, decisionSupport: decisionSupport),
            WendmemWikiJsonContext.Default.McpResponseWikiPageDto);
    }

    [McpServerTool(Name = "WikiWrite"), Description(
        "Create or update a wiki synthesis page. Use when you've produced a non-trivial " +
        "synthesis from multiple drawers, or when a recurring topic deserves a persistent " +
        "compiled artifact. Citations are required — every claim must trace back to source " +
        "drawers, and broken citations get caught by lint. Re-writing the same path is an " +
        "update, not an error.")]
    public async Task<string> WikiWrite(
        WikiStorage wiki,
        ILogger<WikiTools> logger,
        [Description("ASCII kebab-case, slashes for hierarchy, no trailing characters. " +
                     "Stable identity — re-writing the same path overwrites. " +
                     "Example: 'rag/vyer-kommandon', 'auth-flow', 'architecture/storage'.")]
        string path,
        [Description("Human-readable title in sentence case (e.g. 'Wakeup design'), " +
                     "not Title Case ('Wakeup Design'). Shown in indexes and search results.")]
        string title,
        [Description("Markdown body. Use [[other-path]] syntax to link to other wiki pages — " +
                     "backlinks are computed automatically during lint. " +
                     "Aim for 200-600 words; split larger topics into sub-pages.")]
        string content,
        [Description("Comma-separated drawer IDs — each must be exactly 16 hex characters, " +
                     "NOT a file name or path. These come from AddMemory/SearchMemories responses. " +
                     "Example: 'a3f2b1c8d4e5f607,c8e4d2a1b91f0a7e'. " +
                     "The system rejects citations to non-existent drawers.")]
        string citations,
        [Description("Wing namespace (optional — omit to use the configured default wing). " +
                     "ASCII kebab-case, slashes for hierarchy. Example: 'wms', 'rag/supplier'.")]
        string? wing = null,
        [Description("Optional agent identifier for the audit log (e.g. 'claude-code', 'goose-glm').")]
        string? agent = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        wing = PathValidator.ResolveWing(wing, _config);
        var citationList = ParseCitations(citations);
        logger.LogInformation("WikiWrite wing={Wing} path={Path}", wing, path);
        var result = await wiki.WriteAsync(path, wing, title, content, citationList, agent, ct);

        logger.LogInformation("WikiWrite → {Op}  citations={Citations}",
            result == WriteResult.Created ? "created" : "updated", citationList.Count);

        var dto = new WikiWriteResultDto(
            path,
            result == WriteResult.Created ? "created" : "updated");

        var decisionSupport = new McpDecisionSupport(
            CanProceed: true,
            SuggestedAction: SuggestedAction.Proceed,
            Summary: $"Wiki page '{path}' {dto.Result} with {citationList.Count} citations."
        );

        return JsonSerializer.Serialize(
            McpResponse.Ok("WikiWrite", dto, sw.ElapsedMilliseconds, decisionSupport: decisionSupport),
            WendmemWikiJsonContext.Default.McpResponseWikiWriteResultDto);
    }

    [McpServerTool(Name = "WikiSearch"), Description(
        "Search wiki synthesis pages by content, ranked by relevance using hybrid " +
        "BM25 + semantic. Use when WakeUp's recency-ordered page index doesn't surface " +
        "what you need — WikiSearch ranks by relevance, not recency. " +
        "For raw drawer content, use SearchMemories instead.")]
    public async Task<string> WikiSearch(
        WikiStorage wiki,
        ILogger<WikiTools> logger,
        [Description("Search query in natural language. Page content and titles are searched.")]
        string query,
        [Description("Wing to scope to. Recommended.")]
        string? wing = null,
        [Description("Max results. Default 10.")]
        int limit = 10,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        wing = PathValidator.ResolveWing(wing, _config);
        logger.LogInformation("WikiSearch wing={Wing} query={Query}", wing, query);
        var results = await wiki.SearchAsync(query, wing, limit, ct);
        logger.LogInformation("WikiSearch → {Count} pages", results.Count);
        var hits = new List<WikiSearchHitDto>(results.Count);
        foreach (var h in results)
            hits.Add(new WikiSearchHitDto(
                h.Path, h.Title, h.Wing,
                h.UpdatedAt.ToUnixTimeSeconds()));

        var bestScore = results.FirstOrDefault()?.Score ?? 0f;
        var confidence = new McpConfidence(
            Level: bestScore > 0.8f ? "high" : bestScore > 0.5f ? "medium" : "low",
            Score: bestScore,
            Reason: bestScore > 0.5f ? "semantic_match" : "poor_match"
        );

        var decisionSupport = new McpDecisionSupport(
            CanProceed: hits.Count > 0,
            SuggestedAction: hits.Count > 0 ? SuggestedAction.Proceed : SuggestedAction.Retry,
            Summary: hits.Count > 0
                ? $"Found {hits.Count} wiki pages."
                : "No matching wiki pages found."
        );

        return JsonSerializer.Serialize(
            McpResponse.Ok("WikiSearch", new WikiSearchDto(hits), sw.ElapsedMilliseconds, confidence, decisionSupport),
            WendmemWikiJsonContext.Default.McpResponseWikiSearchDto);
    }

    private List<string> ParseCitations(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new List<string>();
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new List<string>(parts);
    }
}
