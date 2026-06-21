using System.Text.RegularExpressions;
using Wendmem.Cli.Commands;
using Wendmem.Storage;
using Wendmem.Wiki;
using Wendmem.Wiki.Models;

namespace Wendmem.Services;

internal sealed partial class GraphDataService(
    DrawerStorage drawers,
    WikiStorage wiki,
    KnowledgeGraph kg,
    EpisodeStorage episodes,
    SkillStorage skills)
{
    public async Task<GraphData> BuildAsync(GraphOptions opts, CancellationToken ct = default)
    {
        var wing = opts.Wing;
        var nodes = new List<GraphNode>();
        var links = new List<GraphLink>();

        // ── Wing node ──────────────────────────────────────────
        nodes.Add(new GraphNode(
            Id: $"wing-{wing}", Type: "wing", Label: wing,
            Wing: wing, Room: null, Source: null, Snippet: null,
            Title: null, Desc: null, Subject: null, Predicate: null,
            Object: null, ValidFrom: null, Outcome: null, NextTime: null,
            SuccessRate: null));

        // ── Wiki pages ─────────────────────────────────────────
        var wikiHeaders = await wiki.IndexAsync(wing, ct);
        var wikiPages = new List<(WikiPageHeader Header, WikiPage? Page)>();

        foreach (var hdr in wikiHeaders)
        {
            if (hdr.Wing != wing)
                continue;

            var page = await wiki.ReadAsync(hdr.Path, ct);
            wikiPages.Add((hdr, page));

            nodes.Add(new GraphNode(
                Id: $"wiki-{hdr.Path}", Type: "wiki", Label: hdr.Path,
                Wing: wing, Room: null, Source: null, Snippet: null,
                Title: hdr.Title, Desc: page?.Content, Subject: null,
                Predicate: null, Object: null, ValidFrom: null,
                Outcome: null, NextTime: null, SuccessRate: null));

            links.Add(new GraphLink(
                Source: $"wing-{wing}", Target: $"wiki-{hdr.Path}",
                Type: "wing"));
        }

        // wiki → wiki cross-links
        foreach (var (hdr, page) in wikiPages)
        {
            if (page is null)
                continue;
            foreach (Match m in WikilinkRegex().Matches(page.Content))
            {
                var target = m.Groups[1].Value;
                links.Add(new GraphLink(
                    Source: $"wiki-{hdr.Path}",
                    Target: $"wiki-{target}",
                    Type: "wiki"));
            }
        }

        // ── KG triples ─────────────────────────────────────────
        if (!opts.NoTriples)
        {
            var triples = await kg.GetActiveTriplesAsync(wing, limit: 200, ct);
            foreach (var t in triples)
            {
                var id = $"triple-{t.Subject}-{t.Predicate}-{t.Object}";
                nodes.Add(new GraphNode(
                    Id: id, Type: "triple",
                    Label: $"{t.Subject} {t.Predicate} {t.Object}",
                    Wing: wing, Room: null, Source: null, Snippet: null,
                    Title: null, Desc: null, Subject: t.Subject,
                    Predicate: t.Predicate, Object: t.Object,
                    ValidFrom: null, Outcome: null, NextTime: null,
                    SuccessRate: null));

                links.Add(new GraphLink(
                    Source: $"wing-{wing}", Target: id,
                    Type: "triple"));
            }
        }

        // ── Drawers ────────────────────────────────────────────
        if (!opts.NoDrawers)
        {
            var drawerList = await drawers.RecentDrawersAsync(wing, opts.Limit, ct);
            // Build citation index: drawer_id → wiki path
            var citationIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (hdr, page) in wikiPages)
            {
                if (page is null)
                    continue;
                foreach (var cite in page.Citations)
                {
                    if (!citationIndex.ContainsKey(cite))
                        citationIndex[cite] = hdr.Path;
                }
            }

            foreach (var d in drawerList)
            {
                if (d.DrawerType == "synthesis")
                    continue;

                var label = d.Id.Length > 8 ? d.Id[..8] + "…" : d.Id;
                var snippet = d.Content.Length > 120
                    ? d.Content[..120] + "…"
                    : d.Content;

                nodes.Add(new GraphNode(
                    Id: $"drw-{d.Id}", Type: "drawer", Label: label,
                    Wing: wing, Room: d.Room,
                    Source: d.Source ?? "", Snippet: snippet,
                    Title: null, Desc: null, Subject: null, Predicate: null,
                    Object: null, ValidFrom: null, Outcome: null, NextTime: null,
                    SuccessRate: null));

                // Link from the most-relevant wiki page or wing
                if (citationIndex.TryGetValue(d.Id, out var wikiPath))
                {
                    links.Add(new GraphLink(
                        Source: $"wiki-{wikiPath}",
                        Target: $"drw-{d.Id}",
                        Type: "wiki"));
                }
                else
                {
                    links.Add(new GraphLink(
                        Source: $"wing-{wing}",
                        Target: $"drw-{d.Id}",
                        Type: "wing"));
                }
            }
        }

        // ── Episodes ───────────────────────────────────────────
        if (!opts.NoEpisodes)
        {
            var epList = await episodes.ListAsync(wing, outcomeFilter: null, limit: 20, ct);
            foreach (var ep in epList)
            {
                var label = ep.Goal.Length > 40
                    ? ep.Goal[..40] + "…"
                    : ep.Goal;

                nodes.Add(new GraphNode(
                    Id: $"ep-{ep.Id}", Type: "episode", Label: label,
                    Wing: wing, Room: null, Source: null, Snippet: null,
                    Title: null, Desc: null, Subject: null, Predicate: null,
                    Object: null, ValidFrom: null, Outcome: ep.Outcome,
                    NextTime: ep.NextTime, SuccessRate: null));

                links.Add(new GraphLink(
                    Source: $"wing-{wing}", Target: $"ep-{ep.Id}",
                    Type: "episode"));
            }
        }

        // ── Skills ─────────────────────────────────────────────
        if (!opts.NoSkills)
        {
            var skillList = await skills.ListAsync(wing, ct);
            foreach (var s in skillList)
            {
                var total = s.SuccessCount + s.FailureCount;
                var rate = total > 0 ? (double)s.SuccessCount / total : (double?)null;

                nodes.Add(new GraphNode(
                    Id: $"skill-{s.Id}", Type: "skill", Label: s.Name,
                    Wing: wing, Room: null, Source: null, Snippet: null,
                    Title: null, Desc: s.Description, Subject: null,
                    Predicate: null, Object: null, ValidFrom: null,
                    Outcome: null, NextTime: null, SuccessRate: rate));

                links.Add(new GraphLink(
                    Source: $"wing-{wing}", Target: $"skill-{s.Id}",
                    Type: "skill"));
            }
        }

        // ── Deduplicate links ──────────────────────────────────
        var uniqueLinks = new HashSet<(string, string)>();
        var dedupedLinks = new List<GraphLink>();
        foreach (var link in links)
        {
            if (uniqueLinks.Add((link.Source, link.Target)))
                dedupedLinks.Add(link);
        }

        return new GraphData(nodes, dedupedLinks, wing, DateTimeOffset.UtcNow);
    }

    [GeneratedRegex(@"\[\[([a-z][a-z0-9-]*(?:/[a-z][a-z0-9-]*)*)\]\]")]
    private static partial Regex WikilinkRegex();
}
