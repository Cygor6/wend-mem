using System.Text;
using System.Text.RegularExpressions;

namespace Wendmem.Services.Okf;

public static partial class OkfFrontmatterParser
{
    [GeneratedRegex(@"^---[ \t]*\r?\n(.*?)\r?\n---[ \t]*(?:\r?\n|$)", RegexOptions.Singleline)]
    private static partial Regex FrontmatterBlockRegex();

    // A key line: starts at column 0, an identifier-ish name, then ':'.
    [GeneratedRegex(@"^([A-Za-z0-9_.\-]+):[ \t]*(.*)$")]
    private static partial Regex KeyLineRegex();

    [GeneratedRegex(@"^[ \t]*-[ \t]+(.*)$")]
    private static partial Regex ListItemRegex();

    public static bool TryParse(
        string content,
        out OkfFrontmatter? frontmatter,
        out string body,
        out string? error)
    {
        frontmatter = null;
        error = null;

        var match = FrontmatterBlockRegex().Match(content);
        if (!match.Success)
        {
            body = content;
            error = "no frontmatter block";
            return false;
        }

        var yaml = match.Groups[1].Value;
        body = content[match.Length..];

        try
        {
            frontmatter = ParseYaml(yaml);
            return true;
        }
        catch (FormatException ex)
        {
            error = $"unparseable frontmatter: {ex.Message}";
            frontmatter = null;
            return false;
        }
    }

    static OkfFrontmatter ParseYaml(string yaml)
    {
        var scalars = new Dictionary<string, string>(StringComparer.Ordinal);
        var lists = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var lines = yaml.Split('\n');

        // State for an empty-value key that may turn out to be a list, a folded
        // scalar, or a null scalar — decided by the first content line that follows.
        string? pendingKey = null;
        bool pendingFolded = false;
        var pendingItems = new List<string>();
        var folded = new StringBuilder();

        void Flush()
        {
            if (pendingKey is null)
                return;
            if (pendingItems.Count > 0)
                lists[pendingKey] = new List<string>(pendingItems);
            else if (pendingFolded)
                scalars[pendingKey] = folded.ToString().Trim();
            else
                scalars[pendingKey] = string.Empty;
            pendingKey = null;
            pendingFolded = false;
            pendingItems.Clear();
            folded.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            var lm = ListItemRegex().Match(line);

            // List item (any indentation, including column 0 as OKF producers emit)
            // belongs to the preceding empty-value key.
            if (lm.Success && pendingKey is not null && !pendingFolded)
            {
                pendingItems.Add(Unquote(lm.Groups[1].Value.Trim()));
                continue;
            }

            // Folded/plain continuation line for a pending scalar.
            if (pendingKey is not null && pendingFolded && char.IsWhiteSpace(line[0]))
            {
                if (folded.Length > 0)
                    folded.Append(' ');
                folded.Append(trimmed);
                continue;
            }

            // Key line at column 0 that is not itself a list item.
            if (!char.IsWhiteSpace(line[0]) && !lm.Success)
            {
                var km = KeyLineRegex().Match(line);
                if (!km.Success)
                    throw new FormatException($"unexpected line '{trimmed}'");

                Flush();

                var key = km.Groups[1].Value;
                var value = km.Groups[2].Value.Trim();

                if (value == ">")
                {
                    pendingKey = key;
                    pendingFolded = true;
                    continue;
                }

                if (value.Length == 0)
                {
                    // Decide list vs folded by peeking the next non-blank line.
                    pendingKey = key;
                    int j = i + 1;
                    while (j < lines.Length && lines[j].TrimEnd('\r').Trim().Length == 0)
                        j++;
                    pendingFolded = j < lines.Length
                        && !ListItemRegex().IsMatch(lines[j].TrimEnd('\r'))
                        && lines[j].Length > 0
                        && char.IsWhiteSpace(lines[j][0]);
                    continue;
                }

                // Inline value (plain or quoted scalar).
                scalars[key] = Unquote(value);
                continue;
            }

            // Anything else (e.g. a list item without a pending key, or a stray
            // indented line) is tolerated rather than aborting the import.
        }

        Flush();
        return Build(scalars, lists);
    }

    static OkfFrontmatter Build(Dictionary<string, string> scalars, Dictionary<string, List<string>> lists)
    {
        scalars.TryGetValue("type", out var type);
        scalars.TryGetValue("title", out var title);
        scalars.TryGetValue("description", out var description);
        scalars.TryGetValue("resource", out var resource);
        scalars.TryGetValue("timestamp", out var timestamp);

        List<string> tags = lists.TryGetValue("tags", out var t) ? t : new();

        // Collect arbitrary producer-defined keys (everything not a known OKF key).
        var known = new HashSet<string>(StringComparer.Ordinal)
        { "type", "title", "description", "resource", "tags", "timestamp" };
        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in scalars)
            if (!known.Contains(kv.Key))
                extra[kv.Key] = kv.Value;
        foreach (var kv in lists)
            if (!known.Contains(kv.Key))
                extra[kv.Key] = string.Join(", ", kv.Value);

        return new OkfFrontmatter(
            Type: type ?? string.Empty,
            Title: title,
            Description: description,
            Resource: resource,
            Tags: tags,
            Timestamp: timestamp,
            Extra: extra);
    }

    /// <summary>Strip a single layer of single/double quotes and unescape the doubled quote.</summary>
    static string Unquote(string value)
    {
        if (value.Length < 2)
            return value;
        if (value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'");
        if (value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"");
        return value;
    }
}

public sealed record OkfFrontmatter(
    string Type,
    string? Title,
    string? Description,
    string? Resource,
    IReadOnlyList<string> Tags,
    string? Timestamp,
    IReadOnlyDictionary<string, string> Extra);
