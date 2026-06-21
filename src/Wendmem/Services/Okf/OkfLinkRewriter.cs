using System.Text.RegularExpressions;
using Wendmem.Wiki;

namespace Wendmem.Services.Okf;

public static partial class OkfLinkRewriter
{
    [GeneratedRegex(@"\[([^\]]*)\]\(([^)]+)\)")]
    private static partial Regex MarkdownLinkRegex();

    // A conventional OKF "Citations" heading at any level — content from this
    // heading onward is preserved verbatim (external URLs / bundle-relative
    // provenance paths, not internal IDs).
    [GeneratedRegex(@"(?m)^#{1,6}\s*Citations\s*$")]
    private static partial Regex CitationsHeadingRegex();

    /// <summary>Rewrite cross-links in <paramref name="body"/>. Returns the new body and the count of rewritten links.</summary>
    public static (string Body, int Rewritten) Rewrite(string body, string conceptDir)
    {
        // Preserve the # Citations section verbatim.
        var citMatch = CitationsHeadingRegex().Match(body);
        string preamble = body;
        string citationsSection = "";
        if (citMatch.Success)
        {
            preamble = body[..citMatch.Index];
            citationsSection = body[citMatch.Index..];
        }

        int rewritten = 0;
        string newPreamble = MarkdownLinkRegex().Replace(preamble, m =>
        {
            var text = m.Groups[1].Value;
            var target = m.Groups[2].Value.Trim();

            var slug = ResolveConceptSlug(target, conceptDir);
            if (slug is null)
                return m.Value; // leave verbatim

            rewritten++;
            return $"[[{slug}]]";
        });

        return (newPreamble + citationsSection, rewritten);
    }

    static string? ResolveConceptSlug(string target, string conceptDir)
    {
        if (target.Length == 0)
            return null;

        // External URLs and protocols.
        if (IsExternal(target))
            return null;

        // Strip an in-page anchor / fragment but remember if the whole target was
        // just an anchor (e.g. "#schema") — those stay verbatim.
        string pathPart = target;
        int hash = target.IndexOf('#');
        if (hash >= 0)
        {
            if (hash == 0)
                return null; // pure anchor "#section"
            pathPart = target[..hash];
        }

        pathPart = pathPart.Trim();
        if (pathPart.Length == 0)
            return null;

        // Only markdown concept links are rewritten.
        if (!IsMarkdownPath(pathPart))
            return null;

        // Reserved index.md is never a concept page.
        var fileName = FileName(pathPart);
        if (fileName.Equals("index.md", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("index.markdown", StringComparison.OrdinalIgnoreCase))
            return null;

        bool absolute = pathPart[0] == '/' || pathPart[0] == '\\';
        var rel = absolute ? pathPart[1..] : pathPart;

        // Resolve relative links against the concept's directory.
        if (!absolute)
        {
            var resolved = ResolveRelative(rel, conceptDir);
            if (resolved is null)
                return null; // escapes the bundle
            rel = resolved;
        }

        // Drop the extension, then slugify the bundle-relative concept path.
        rel = StripMarkdownExtension(rel);
        return PathValidator.Slugify(rel);
    }

    static bool IsExternal(string target)
        => target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
           || target.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);

    static bool IsMarkdownPath(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower.Contains(".md") || lower.Contains(".markdown");
    }

    static string FileName(string path)
    {
        var norm = path.Replace('\\', '/');
        var idx = norm.LastIndexOf('/');
        return idx >= 0 ? norm[(idx + 1)..] : norm;
    }

    static string StripMarkdownExtension(string path)
    {
        var norm = path.Replace('\\', '/');
        if (norm.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
            return norm[..^9];
        if (norm.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return norm[..^3];
        return norm;
    }

    /// <summary>
    /// Resolve a relative path against a base directory, collapsing '.' and '..'.
    /// Returns null when '..' would escape above the bundle root.
    /// </summary>
    static string? ResolveRelative(string rel, string baseDir)
    {
        var segments = new List<string>();
        foreach (var seg in baseDir.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            segments.Add(seg);

        foreach (var seg in rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".")
                continue;
            if (seg == "..")
            {
                if (segments.Count == 0)
                    return null; // escapes the bundle
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(seg);
        }

        return string.Join('/', segments);
    }
}
