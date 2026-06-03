using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Wendmem.Storage;

namespace Wendmem.Wiki;

public sealed record LintFinding(
    string Rule, string Severity, string PagePath, string Message,
    Dictionary<string, string?> Details);

public sealed record LintReport(
    string? Wing, int PageCount, IReadOnlyList<LintFinding> Findings)
{
    public Dictionary<string, int> CountsByRule =>
        Findings.GroupBy(f => f.Rule)
            .ToDictionary(g => g.Key, g => g.Count());
}

sealed partial class WikiLinter(
    DuckDbConnectionFactory dbFactory,
    PendingUpdateService pending,
    ILogger<WikiLinter> logger)
{
    public async Task<LintReport> LintAsync(string? wing, CancellationToken ct = default)
    {
        var pages = await LoadPagesAsync(wing, ct);
        var findings = new List<LintFinding>();

        if (pages.Count == 0)
        {
            await FindGapCandidatesAsync([], findings, ct);
            return new LintReport(wing, 0, findings);
        }

        var existingDrawerIds = await LoadActiveDrawerIdsAsync(ct);
        var allPagePaths = pages.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var outboundByPage = pages.ToDictionary(
            p => p.Path,
            p => ExtractWikiLinks(p.Content),
            StringComparer.OrdinalIgnoreCase);

        var inboundMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            if (!inboundMap.ContainsKey(page.Path))
                inboundMap[page.Path] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in outboundByPage[page.Path])
            {
                if (!inboundMap.ContainsKey(link))
                    inboundMap[link] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                inboundMap[link].Add(page.Path);
            }
        }

        var pendingCounts = await LoadPendingCountsAsync(wing, ct);

        foreach (var page in pages)
        {
            var outboundLinks = outboundByPage[page.Path];

            // BrokenCitation
            foreach (var cit in page.Citations)
            {
                if (!existingDrawerIds.Contains(cit))
                {
                    findings.Add(new LintFinding(
                        "BrokenCitation", "error", page.Path,
                        $"Citation {cit} references a non-existent or retired drawer.",
                        new() { ["drawer_id"] = cit }));
                }
            }

            // OrphanPage
            var hasInbound = inboundMap.TryGetValue(page.Path, out var inbounders) && inbounders.Count > 0;
            if (outboundLinks.Count == 0 && !hasInbound)
            {
                findings.Add(new LintFinding(
                    "OrphanPage", "warn", page.Path,
                    "Page has no inbound or outbound wikilinks.",
                    new()));
            }

            // StalePage — all cited drawers are retired
            if (page.Citations.Count > 0)
            {
                var allRetired = await AllCitationsRetiredAsync(page.Citations, ct);
                if (allRetired)
                {
                    findings.Add(new LintFinding(
                        "StalePage", "warn", page.Path,
                        "All cited drawers are retired or have valid_to set.",
                        new()));
                }
            }

            // MissingCrossLink
            foreach (var otherPage in pages)
            {
                if (otherPage.Path == page.Path)
                    continue;
                var title = otherPage.Title;
                if (!string.IsNullOrEmpty(title) &&
                    page.Content.Contains(title, StringComparison.OrdinalIgnoreCase) &&
                    !outboundLinks.Contains(otherPage.Path, StringComparer.OrdinalIgnoreCase))
                {
                    findings.Add(new LintFinding(
                        "MissingCrossLink", "info", page.Path,
                        $"Page mentions '{title}' but does not link to [[{otherPage.Path}]].",
                        new() { ["mentioned_title"] = title, ["suggested_link"] = otherPage.Path }));
                }
            }

            // PendingUpdates — page has >= 3 unresolved pending updates
            if (pendingCounts.TryGetValue(page.Path, out var pc) && pc >= 3)
            {
                findings.Add(new LintFinding(
                    "PendingUpdates", "info", page.Path,
                    $"Page has {pc} unresolved pending update(s) from new drawers.",
                    new() { ["pending_count"] = pc.ToString() }));
            }

            // LowQuality
            if (page.QualityScore < 0.5f)
            {
                findings.Add(new LintFinding(
                    "LowQuality", "warn", page.Path,
                    $"Page has quality score {page.QualityScore:F2} — consider adding citations, wikilinks, or expanding content.",
                    new()
                    {
                        ["quality_score"] = page.QualityScore.ToString("F2"),
                        ["word_count"] = page.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length.ToString(),
                        ["has_citations"] = (page.Citations.Count > 0).ToString().ToLowerInvariant()
                    }));
            }

            // ContradictionCandidate
            await CheckContradictionsAsync(page, pending, wing, findings, ct);
        }

        // GapCandidate — entities with >= 5 triples but no wiki page
        await FindGapCandidatesAsync(allPagePaths, findings, ct);

        return new LintReport(wing, pages.Count, findings);
    }

    async Task<List<PageData>> LoadPagesAsync(string? wing, CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        var wingFilter = wing is not null ? "WHERE wing = $wing" : "";
        cmd.CommandText = $"""
            SELECT path, wing, title, content, citations, COALESCE(quality_score, 0.0)
            FROM wiki_pages
            {wingFilter}
            ORDER BY path
            """;
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));

        var list = new List<PageData>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new PageData(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ReadStringArray(reader, 4),
                reader.GetFloat(5)));
        }
        return list;
    }

    async Task<HashSet<string>> LoadActiveDrawerIdsAsync(CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = "SELECT id FROM drawers WHERE is_representative = TRUE AND valid_to IS NULL";
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetString(0));
        return ids;
    }

    async Task<Dictionary<string, int>> LoadPendingCountsAsync(string? wing, CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        var wingFilter = wing is not null ? "AND wing = $wing" : "";
        cmd.CommandText = $"""
            SELECT page_path, count(*) AS cnt
            FROM wiki_pending_updates
            WHERE resolved_at IS NULL {wingFilter}
            GROUP BY page_path
            """;
        if (wing is not null)
            cmd.Parameters.Add(new DuckDBParameter("wing", wing));

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = (int)reader.GetInt64(1);
        return result;
    }

    async Task<bool> AllCitationsRetiredAsync(IReadOnlyList<string> citations, CancellationToken ct)
    {
        if (citations.Count == 0)
            return false;

        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        var inList = string.Join(",", citations.Select(c => $"'{c}'"));
        cmd.CommandText = $"""
            SELECT count(*) FROM drawers
            WHERE id IN ({inList})
              AND is_representative = TRUE
              AND valid_to IS NULL
            """;
        var active = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return active == 0;
    }

    async Task CheckContradictionsAsync(
        PageData page, PendingUpdateService pendingService, string? wing,
        List<LintFinding> findings, CancellationToken ct)
    {
        var pendingRows = await pendingService.ListPendingAsync(wing, page.Path, limit: 100, ct);
        if (pendingRows.Count == 0)
            return;

        var pageTriples = await LoadCitedTriplesAsync(page.Citations, ct);

        foreach (var pu in pendingRows)
        {
            if (pu.Similarity < 0.55f)
                continue;

            var drawerTriples = await LoadDrawerTriplesAsync(pu.DrawerId, ct);

            foreach (var dt in drawerTriples)
            {
                foreach (var pt in pageTriples)
                {
                    if (string.Equals(dt.Subject, pt.Subject, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(dt.Predicate, pt.Predicate, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(dt.Object, pt.Object, StringComparison.OrdinalIgnoreCase) &&
                        (IsNumeric(dt.Object) || IsNumeric(pt.Object)))
                    {
                        findings.Add(new LintFinding(
                            "ContradictionCandidate", "warn", page.Path,
                            $"KG triple conflict: {dt.Subject} {dt.Predicate} differs " +
                            $"('{pt.Object}' vs '{dt.Object}') from drawer {pu.DrawerId}.",
                            new()
                            {
                                ["drawer_id"] = pu.DrawerId,
                                ["subject"] = dt.Subject,
                                ["predicate"] = dt.Predicate,
                                ["existing_object"] = pt.Object,
                                ["new_object"] = dt.Object
                            }));
                        return; // one contradiction finding per page is enough
                    }
                }
            }
        }
    }

    async Task FindGapCandidatesAsync(
        HashSet<string> allPagePaths, List<LintFinding> findings,
        CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT e.name, count(*) AS cnt
            FROM entities e
            JOIN triples t ON e.id = t.subject OR e.id = t.object
            WHERE t.valid_to IS NULL
            GROUP BY e.name
            HAVING count(*) >= 5
            ORDER BY cnt DESC
            LIMIT 50
            """;

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var count = reader.GetInt64(1);

            // Check if any page path contains the entity name (spaces → hyphens)
            var hasPage = allPagePaths.Any(p =>
                p.Contains(name.Replace(' ', '-'), StringComparison.OrdinalIgnoreCase));

            if (!hasPage)
            {
                findings.Add(new LintFinding(
                    "GapCandidate", "info", "",
                    $"Entity '{name}' has {count} triples but no wiki page.",
                    new() { ["entity"] = name, ["triple_count"] = count.ToString() }));
            }
        }
    }

    async Task<List<(string Subject, string Predicate, string Object)>> LoadDrawerTriplesAsync(
        string drawerId, CancellationToken ct)
    {
        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = """
            SELECT subject, predicate, object FROM triples
            WHERE drawer_id = $drawer_id AND valid_to IS NULL
            """;
        cmd.Parameters.Add(new DuckDBParameter("drawer_id", drawerId));

        var list = new List<(string, string, string)>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }

    async Task<List<(string Subject, string Predicate, string Object)>> LoadCitedTriplesAsync(
        IReadOnlyList<string> drawerIds, CancellationToken ct)
    {
        if (drawerIds.Count == 0)
            return [];
        var inList = string.Join(",", drawerIds.Select(id => $"'{id}'"));

        using var ro = dbFactory.OpenReadOnly();
        using var cmd = ro.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT subject, predicate, object FROM triples
            WHERE drawer_id IN ({inList}) AND valid_to IS NULL
            """;

        var list = new List<(string, string, string)>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }

    static List<string> ExtractWikiLinks(string content)
    {
        var links = new List<string>();
        foreach (Match m in WikilinkRegex().Matches(content))
            links.Add(m.Groups[1].Value);
        return links;
    }

    [GeneratedRegex(@"\[\[([a-z][a-z0-9-]*(?:/[a-z][a-z0-9-]*)*)\]\]")]
    private static partial Regex WikilinkRegex();

    static bool IsNumeric(string s) =>
        s.Any(c => char.IsDigit(c)) &&
        s.All(c => char.IsDigit(c) || c == '.' || c == '-' || c == '+' || c == 'e' || c == 'E');

    static IReadOnlyList<string> ReadStringArray(System.Data.Common.DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return [];
        var value = reader.GetValue(ordinal);
        return value switch
        {
            string[] arr => arr,
            object[] objArr => objArr.OfType<string>().ToArray(),
            System.Collections.ICollection col => col.Cast<string>().ToList(),
            _ => []
        };
    }

    record PageData(string Path, string Wing, string Title, string Content, IReadOnlyList<string> Citations, float QualityScore);
}
