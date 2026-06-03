using System.Text;
using ModelContextProtocol.Server;
using Wendmem.Services;
using Wendmem.Storage;

namespace Wendmem.Wiki;

static class PalaceSchemaResource
{
    [McpServerResource(UriTemplate = "palace://schema", Name = "palace-schema",
        Title = "Palace schema and conventions")]
    public static async Task<string> GetSchema(
        PalaceConfig config,
        DrawerStorage storage,
        KnowledgeGraph kg,
        HallDetector hallDetector,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# This palace");
        sb.AppendLine();

        // Wing counts
        var wingsRooms = await storage.ListWingsRoomsAsync(ct);
        var wingGroups = wingsRooms.GroupBy(wr => wr.Wing);
        var wingCounts = wingGroups.Select(g => $"{g.Key} ({g.Count()} rooms)").ToList();
        sb.AppendLine($"Wings: {string.Join(", ", wingCounts)}.");
        sb.AppendLine();

        // Routing keywords
        sb.AppendLine("## Routing");
        sb.AppendLine();
        sb.AppendLine("When the user mentions any of these keywords, you are operating in the named wing:");
        foreach (var (wing, keywords) in hallDetector.GetAllMappings())
            sb.AppendLine($"- **{wing}**: {string.Join(", ", keywords)}");
        sb.AppendLine();

        // Conventions
        sb.AppendLine("## Conventions");
        sb.AppendLine();
        sb.AppendLine("- Drawers are verbatim. Never paraphrase content into a drawer; mine the source instead.");
        sb.AppendLine("- Wiki pages must cite drawer IDs. One paragraph minimum, 200-600 words target, sentence-case titles.");
        sb.AppendLine("- KG predicates are snake_case. Subjects/objects lowercase entity names.");
        sb.AppendLine();

        // Tool surface
        sb.AppendLine("## Tools (17)");
        sb.AppendLine();
        sb.AppendLine("### Memory & search");
        sb.AppendLine("- `WakeUp(wing)` — compact palace map: synthesis pages, recent drawers, KG facts, episodes, skills.");
        sb.AppendLine("- `SearchMemories(query, wing?, room?, k?)` — hybrid BM25 + semantic over raw drawers. For concepts.");
        sb.AppendLine("- `GrepExact(pattern, wing?, room?, k?)` — exact string/regex (RE2) over drawer content. For symbols/IDs.");
        sb.AppendLine("- `GetDrawer(id)` — retrieve a single drawer by 16-hex-char ID.");
        sb.AppendLine("- `AddMemory(content, wing, room, ...)` — store text as an immutable drawer.");
        sb.AppendLine();
        sb.AppendLine("### Knowledge graph");
        sb.AppendLine("- `AddTriple(subject, predicate, object, ...)` — add a KG fact with conflict detection.");
        sb.AppendLine("- `InvalidateTriple(subject, predicate, object)` — soft-delete a KG triple.");
        sb.AppendLine();
        sb.AppendLine("### Wiki");
        sb.AppendLine("- `WikiRead(path)` — read a wiki synthesis page by path.");
        sb.AppendLine("- `WikiWrite(path, wing, title, content, citations)` — create or update a wiki page.");
        sb.AppendLine("- `WikiSearch(query, wing?, limit?)` — hybrid search over wiki pages.");
        sb.AppendLine("- `LintWiki(wing)` — check orphans, broken citations, stale pages.");
        sb.AppendLine("- `Distill(wing, sessionSummary)` — scaffold wiki pages from session summary.");
        sb.AppendLine("- `ListPendingUpdates(wing?)` — pages with queued evidence since last write.");
        sb.AppendLine("- `DismissPendingUpdate(path)` — dismiss a pending update.");
        sb.AppendLine();
        sb.AppendLine("### Episodes & skills");
        sb.AppendLine("- `RecordEpisode(wing, goal, plan, outcome, ...)` — capture what worked/failed this session.");
        sb.AppendLine("- `FindEpisodes(query, wing?, k?)` — retrieve past episodes by relevance.");
        sb.AppendLine("- `FindSkills(query, wing?)` — discover procedural skills for the current task.");
        sb.AppendLine();

        // Workflow
        sb.AppendLine("## Workflow");
        sb.AppendLine();
        sb.AppendLine("Session start:");
        sb.AppendLine("1. Call `WakeUp(wing)` and read every section, including `pending_updates`.");
        sb.AppendLine("2. If `pending_updates` is non-empty, plan whether to address those during this session.");
        sb.AppendLine();
        sb.AppendLine("During work:");
        sb.AppendLine("- For information that fits in one paragraph and concerns a relationship → `AddTriple`.");
        sb.AppendLine("- For verbatim user statements that should persist → `AddMemory`.");
        sb.AppendLine("- For synthesis across multiple drawers → `WikiWrite` with citations.");
        sb.AppendLine("- For exact symbols/IDs/errors → `GrepExact`. For concepts → `SearchMemories`.");
        sb.AppendLine();
        sb.AppendLine("Session end (REQUIRED for non-trivial tasks):");
        sb.AppendLine("1. Call `RecordEpisode(wing, goal, plan, outcome, ...)` — captures what worked/failed.");
        sb.AppendLine("2. Call `Distill(wing, sessionSummary)` — crystallizes session into wiki pages.");
        sb.AppendLine("3. Decide: update an existing page, create a new one, or no action.");
        sb.AppendLine("4. If updating/creating, call `WikiWrite`.");
        sb.AppendLine();
        sb.AppendLine("Episodes and skills:");
        sb.AppendLine("- `RecordEpisode` before Distill when a non-trivial task completes (success or failure).");
        sb.AppendLine("- `FindEpisodes(query)` for narrower lookups beyond WakeUp's episode field.");
        sb.AppendLine("- `FindSkills(query)` to discover procedural skills for the current task.");
        sb.AppendLine("- Skills are SKILL.md folders on disk; use `wendmem skills reindex` CLI to register.");
        sb.AppendLine();
        sb.AppendLine("Reflection (CLI-driven, not MCP):");
        sb.AppendLine("- `wendmem reflect run --wing W` — LLM-driven synthesis of recent drawers into wiki drafts.");
        sb.AppendLine("- Drafts surface in WakeUp's `reflection_drafts` field for agent review.");
        sb.AppendLine("- Accept via `wendmem reflect drafts accept <id>` — writes to wiki automatically.");
        sb.AppendLine();
        sb.AppendLine("Maintenance (run at most once per session, when convenient):");
        sb.AppendLine("- `LintWiki(wing)` - work through findings until empty or until you've spent ~5 minutes.");
        sb.AppendLine();

        // ACDL context specification as fenced code block
        var wingNames = string.Join(", ", wingGroups.Select(g => g.Key));
        sb.AppendLine("## ACDL context specification");
        sb.AppendLine();
        sb.AppendLine("```acdl");
        sb.AppendLine("// wings: " + wingNames);
        sb.AppendLine("""
            WakeUp [@T]: {
            S: KG_FACTS(sys.wing[@T])          // active triples, valid_to IS NULL
            S: WIKI_INDEX(sys.wing[@T])         // page paths + titles
            S: PENDING_UPDATES(sys.wing[@T])    // pages with queued evidence counts
            U: {
              ForEach (p: synthesis_pages(sys.wing[@T])) { p.content }
              ForEach (d: recent_drawers(sys.wing[@T], sys.conf.wakeup_recency_count)) { d.content }
            }
            Name hits := hybrid_search(env.seed_query[@T], sys.wing[@T])
            ForEach (i: range(1, hits.len)) { hits[i].content }
            }

            Distill [@T -> @T+1]:
              Session boundary event. Must be called before agent state resets.
              Crystallizes episodic memory from @T into persistent wiki pages.
            """);
        sb.AppendLine("```");

        return sb.ToString().TrimEnd();
    }
}
