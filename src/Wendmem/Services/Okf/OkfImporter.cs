using System.Text;
using Microsoft.Extensions.Logging;
using Wendmem.Storage;
using Wendmem.Wiki;

namespace Wendmem.Services.Okf;

internal sealed class OkfImporter
{
    readonly DrawerStorage _drawers;
    readonly Chunkers.TopicShiftChunker _chunker;
    readonly KnowledgeGraph _kg;
    readonly WikiStorage _wiki;
    readonly ActivityLog _activity;
    readonly PalaceConfig _config;
    readonly ILogger<OkfImporter> _logger;

    public OkfImporter(
        DrawerStorage drawers,
        Chunkers.TopicShiftChunker chunker,
        KnowledgeGraph kg,
        WikiStorage wiki,
        ActivityLog activity,
        PalaceConfig config,
        ILogger<OkfImporter> logger)
    {
        _drawers = drawers;
        _chunker = chunker;
        _kg = kg;
        _wiki = wiki;
        _activity = activity;
        _config = config;
        _logger = logger;
    }

    public async Task<OkfImportReport> ImportAsync(
        string bundleRoot, string? wing, string? room, bool dryRun, CancellationToken ct)
    {
        wing = PathValidator.ResolveWing(wing, _config);
        room = string.IsNullOrWhiteSpace(room) ? "okf" : PathValidator.ValidateRoom(room);

        var files = EnumerateConceptCandidates(bundleRoot);
        var perConcept = new List<OkfConceptResult>();

        foreach (var (conceptId, absPath, conceptDir) in files)
        {
            var result = await ImportConceptAsync(
                bundleRoot, conceptId, absPath, conceptDir, wing, room, dryRun, ct);
            perConcept.Add(result);
        }

        int imported = perConcept.Count(r => !r.Skipped);
        int skipped = perConcept.Count - imported;
        int totalDrawers = perConcept.Sum(r => r.DrawerCount);
        int totalTriples = perConcept.Sum(r => r.TripleCount);

        return new OkfImportReport(
            Wing: wing,
            Room: room,
            BundleRoot: bundleRoot,
            ConceptsFound: perConcept.Count,
            ConceptsImported: imported,
            ConceptsSkipped: skipped,
            TotalDrawers: totalDrawers,
            TotalTriples: totalTriples,
            DryRun: dryRun,
            Concepts: perConcept);
    }

    async Task<OkfConceptResult> ImportConceptAsync(
        string bundleRoot, string conceptId, string absPath, string conceptDir,
        string wing, string room, bool dryRun, CancellationToken ct)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(absPath, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OKF: failed to read {Path}", absPath);
            return Skipped(conceptId, absPath, $"unreadable file: {ex.Message}");
        }

        if (!OkfFrontmatterParser.TryParse(content, out var fm, out var body, out var parseError)
            || fm is null)
        {
            return Skipped(conceptId, absPath, parseError ?? "unparseable frontmatter");
        }

        if (string.IsNullOrWhiteSpace(fm.Type))
            return Skipped(conceptId, absPath, "empty type");

        // Type is non-empty but may be an unfamiliar value — tolerated, never skipped.
        var chunks = await _chunker.ChunkAsync(body, ct);
        if (chunks.Count == 0)
            chunks = [FallbackChunk(fm, body)];

        // Link rewriting (read-only; identical in dry-run and real import).
        var (wikiBody, rewritten) = OkfLinkRewriter.Rewrite(body, conceptDir);
        string title = ResolveTitle(fm, conceptId);
        DateOnly? validFrom = TryParseDate(fm.Timestamp);

        int drawerCount = chunks.Count;
        int tripleCount = CountTriples(fm);

        if (dryRun)
        {
            return new OkfConceptResult(
                conceptId, absPath, fm.Type, title,
                Skipped: false, SkipReason: null,
                DrawerCount: drawerCount, TripleCount: tripleCount,
                RewrittenLinks: rewritten);
        }

        // 1. Body → source drawers (faithful body chunks), collect citation IDs.
        // Provenance is captured by the drawer `source` column (absPath); the body is
        // mined verbatim so Phase 2 can measure the type-retrieval gap cleanly.
        var citationIds = new List<string>(chunks.Count);
        foreach (var chunk in chunks)
        {
            try
            {
                var adm = await _drawers.AddDrawerAsync(
                    chunk, wing, room, absPath,
                    sourceMtime: null, drawerType: "source", ct: ct);

                // Admitted → its id; rejected as near-duplicate → the matched
                // representative drawer is valid provenance. A short/no-signal chunk
                // rejected with no match cannot be cited; the fallback chunk covers this.
                var id = adm.Admitted ? adm.Id : (adm.MatchedId ?? adm.Id);
                if (!string.IsNullOrEmpty(id))
                    citationIds.Add(id);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OKF: failed to add drawer for {Path}", absPath);
            }
        }

        // Guarantee at least one citation (the wiki citations field is provenance).
        if (citationIds.Count == 0)
        {
            var adm = await _drawers.AddDrawerAsync(
                FallbackChunk(fm, body), wing, room, absPath, null, "source", ct: ct);
            var fbId = adm.Admitted ? adm.Id : (adm.MatchedId ?? adm.Id);
            if (!string.IsNullOrEmpty(fbId))
                citationIds.Add(fbId);
            drawerCount = Math.Max(drawerCount, 1);
        }

        var distinctCitations = citationIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // 2. Frontmatter → KG triples, wing-scoped via the first citation drawer.
        var anchorDrawer = distinctCitations[0];
        int actualTriples = await EmitTriplesAsync(conceptId, fm, anchorDrawer, absPath, wing, room, validFrom, ct);

        // 3. Body → wiki page (link-rewritten; description lead; # Citations verbatim).
        var pageContent = BuildPageContent(fm, wikiBody);
        try
        {
            await _wiki.WriteAsync(
                PathValidator.Slugify(conceptId) ?? conceptId,
                wing, title, pageContent, distinctCitations, "okf-import", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OKF: WikiWrite failed for {Path}", absPath);
        }

        // 4. Activity.
        await _activity.LogAsync("okf_import", wing, absPath, "okf-import",
            $"Imported concept '{conceptId}' (type: {fm.Type}, {distinctCitations.Count} drawers, {actualTriples} triples)", ct);

        return new OkfConceptResult(
            conceptId, absPath, fm.Type, title,
            Skipped: false, SkipReason: null,
            DrawerCount: distinctCitations.Count, TripleCount: actualTriples,
            RewrittenLinks: rewritten);
    }

    async Task<int> EmitTriplesAsync(
        string conceptId, OkfFrontmatter fm, string drawerId, string absPath,
        string wing, string room, DateOnly? validFrom, CancellationToken ct)
    {
        int emitted = 0;
        await TryTriple(conceptId, "has_type", fm.Type.Trim(), drawerId, absPath, wing, room, validFrom, ct, _ => emitted++);
        if (!string.IsNullOrWhiteSpace(fm.Resource))
            await TryTriple(conceptId, "resource", fm.Resource!.Trim(), drawerId, absPath, wing, room, validFrom, ct, _ => emitted++);
        foreach (var tag in fm.Tags)
            if (!string.IsNullOrWhiteSpace(tag))
                await TryTriple(conceptId, "tagged", tag.Trim(), drawerId, absPath, wing, room, validFrom, ct, _ => emitted++);
        return emitted;
    }

    async Task TryTriple(
        string subject, string predicate, string obj,
        string drawerId, string absPath, string wing, string room,
        DateOnly? validFrom, CancellationToken ct, Action<string> onOk)
    {
        try
        {
            await _kg.AddTripleAsync(
                subject, predicate, obj,
                validFrom: validFrom,
                sourceRoom: room,
                sourceFile: absPath,
                drawerId: drawerId,
                sourceRef: "okf",
                ct: ct);
            onOk(obj);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OKF: AddTriple failed {S} {P} {O}", subject, predicate, obj);
        }
    }

    static int CountTriples(OkfFrontmatter fm)
    {
        int n = 1; // has_type (type is non-empty here)
        if (!string.IsNullOrWhiteSpace(fm.Resource))
            n++;
        n += fm.Tags.Count(t => !string.IsNullOrWhiteSpace(t));
        return n;
    }

    static string BuildPageContent(OkfFrontmatter fm, string rewrittenBody)
    {
        var sb = new StringBuilder();

        // Compact, search-friendly metadata line.
        var meta = new StringBuilder();
        meta.Append("Type: ").Append(fm.Type.Trim());
        if (fm.Tags.Count > 0)
            meta.Append(" | Tags: ").Append(string.Join(", ", fm.Tags.Where(t => !string.IsNullOrWhiteSpace(t))));
        if (!string.IsNullOrWhiteSpace(fm.Resource))
            meta.Append(" | Resource: ").Append(fm.Resource!.Trim());
        sb.AppendLine(meta.ToString()).AppendLine();

        if (!string.IsNullOrWhiteSpace(fm.Description)
            && fm.Description!.Trim().Length >= 10)
        {
            sb.Append(">").AppendLine(fm.Description.Trim()).AppendLine();
        }
        sb.Append(rewrittenBody.Trim());
        return sb.ToString().TrimEnd();
    }

    static string ResolveTitle(OkfFrontmatter fm, string conceptId)
    {
        if (!string.IsNullOrWhiteSpace(fm.Title))
            return fm.Title!.Trim();

        var last = conceptId.Replace('\\', '/');
        var idx = last.LastIndexOf('/');
        if (idx >= 0)
            last = last[(idx + 1)..];
        last = last.Replace('-', ' ').Replace('_', ' ').Trim();
        if (last.Length == 0)
            return conceptId;
        return char.ToUpperInvariant(last[0]) + last[1..];
    }

    static string FallbackChunk(OkfFrontmatter fm, string body)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(fm.Title))
            sb.Append("# ").AppendLine(fm.Title.Trim());
        if (!string.IsNullOrWhiteSpace(fm.Description))
            sb.AppendLine(fm.Description!.Trim());
        if (!string.IsNullOrWhiteSpace(body))
            sb.Append(body.Trim());
        var s = sb.ToString().Trim();
        return s.Length == 0 ? $"# {fm.Type}" : s;
    }

    static DateOnly? TryParseDate(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
            return null;
        if (DateTimeOffset.TryParse(timestamp.Trim(), out var dto))
            return DateOnly.FromDateTime(dto.UtcDateTime);
        return null;
    }

    static List<(string ConceptId, string AbsPath, string ConceptDir)> EnumerateConceptCandidates(string bundleRoot)
    {
        var result = new List<(string, string, string)>();
        if (!Directory.Exists(bundleRoot))
            return result;

        foreach (var abs in Directory.EnumerateFiles(bundleRoot, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(abs);
            if (!ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = Path.GetFileName(abs);
            if (name.Equals("index.md", StringComparison.OrdinalIgnoreCase)
                || name.Equals("index.markdown", StringComparison.OrdinalIgnoreCase)
                || name.Equals("log.md", StringComparison.OrdinalIgnoreCase))
                continue;

            var rel = Path.GetRelativePath(bundleRoot, abs).Replace('\\', '/');
            var conceptId = rel.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)
                ? rel[..^9]
                : rel[..^3];

            var dirIdx = conceptId.LastIndexOf('/');
            var conceptDir = dirIdx >= 0 ? conceptId[..dirIdx] : "";

            result.Add((conceptId, abs, conceptDir));
        }

        result.Sort(static (a, b) => string.Compare(a.Item1, b.Item1, StringComparison.Ordinal));
        return result;
    }

    static OkfConceptResult Skipped(string conceptId, string path, string reason) =>
        new(conceptId, path, "", null,
            Skipped: true, SkipReason: reason,
            DrawerCount: 0, TripleCount: 0, RewrittenLinks: 0);
}

public sealed record OkfConceptResult(
    string ConceptId,
    string Path,
    string Type,
    string? Title,
    bool Skipped,
    string? SkipReason,
    int DrawerCount,
    int TripleCount,
    int RewrittenLinks);

public sealed record OkfImportReport(
    string Wing,
    string Room,
    string BundleRoot,
    int ConceptsFound,
    int ConceptsImported,
    int ConceptsSkipped,
    int TotalDrawers,
    int TotalTriples,
    bool DryRun,
    IReadOnlyList<OkfConceptResult> Concepts);
