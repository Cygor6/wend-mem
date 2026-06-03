using System.Text;
using System.Text.RegularExpressions;

namespace Wendmem.Services;

public sealed partial class SkillFrontmatterParser
{
    [GeneratedRegex(@"^---\s*\r?\n(.*?)\r?\n---\s*\r?\n", RegexOptions.Singleline)]
    private static partial Regex FrontmatterBlockRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9-]*$")]
    private static partial Regex KebabCaseRegex();

    [GeneratedRegex(@"[<>]")]
    private static partial Regex ForbiddenCharsRegex();

    /// <summary>
    /// Parse a SKILL.md file's frontmatter. Returns the parsed frontmatter
    /// and the body content (everything after the closing ---).
    /// </summary>
    public static SkillFrontmatter Parse(string content, string folderName, string filePath)
    {
        // Validate filename
        if (!Path.GetFileName(filePath).Equals("SKILL.md", StringComparison.Ordinal))
            throw new FormatException($"File must be named exactly 'SKILL.md', got '{Path.GetFileName(filePath)}'");

        // Validate folder name is kebab-case
        if (!KebabCaseRegex().IsMatch(folderName))
            throw new FormatException(
                $"Folder name '{folderName}' must be kebab-case (^[a-z][a-z0-9-]*$). Path: {filePath}");

        var match = FrontmatterBlockRegex().Match(content);
        if (!match.Success)
            throw new FormatException(
                $"SKILL.md must start with YAML frontmatter delimited by '---' on their own lines. Path: {filePath}");

        var yaml = match.Groups[1].Value;
        var body = content[match.Length..];

        var fields = ParseSimpleYaml(yaml);

        // Required: name
        if (!fields.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
            throw new FormatException($"Frontmatter must have a 'name' field. Path: {filePath}");

        // name must equal folder name
        if (!name.Equals(folderName, StringComparison.Ordinal))
            throw new FormatException(
                $"Frontmatter name '{name}' must match folder name '{folderName}'. Path: {filePath}");

        // name must not contain 'claude' or 'anthropic'
        if (name.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("anthropic", StringComparison.OrdinalIgnoreCase))
            throw new FormatException(
                $"Skill name must not contain 'claude' or 'anthropic'. Got: '{name}'. Path: {filePath}");

        // Required: description
        if (!fields.TryGetValue("description", out var description) || string.IsNullOrWhiteSpace(description))
            throw new FormatException($"Frontmatter must have a 'description' field. Path: {filePath}");

        // Description max 1024 chars, no < or >
        if (description.Length > 1024)
            throw new FormatException(
                $"Description must be ≤1024 chars, got {description.Length}. Path: {filePath}");

        if (ForbiddenCharsRegex().IsMatch(description))
            throw new FormatException(
                $"Description must not contain '<' or '>'. Path: {filePath}");

        fields.TryGetValue("license", out var license);
        fields.TryGetValue("compatibility", out var compatibility);

        // Collect any extra fields as metadata
        var knownKeys = new HashSet<string> { "name", "description", "license", "compatibility" };
        var metadata = fields
            .Where(kv => !knownKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return new SkillFrontmatter(name, description.Trim(), license, compatibility, metadata.Count > 0 ? metadata : null);
    }

    /// <summary>
    /// Validate a SKILL.md without throwing — returns list of issues.
    /// </summary>
    public static List<string> Validate(string content, string folderName, string filePath)
    {
        var issues = new List<string>();

        if (!Path.GetFileName(filePath).Equals("SKILL.md", StringComparison.Ordinal))
            issues.Add($"File must be named exactly 'SKILL.md' (case-sensitive), got '{Path.GetFileName(filePath)}'");

        if (!KebabCaseRegex().IsMatch(folderName))
            issues.Add($"Folder name '{folderName}' must be kebab-case (^[a-z][a-z0-9-]*$)");

        var match = FrontmatterBlockRegex().Match(content);
        if (!match.Success)
        {
            issues.Add("Missing or malformed frontmatter block (needs '---' delimiters on their own lines)");
            return issues;
        }

        var yaml = match.Groups[1].Value;
        Dictionary<string, string> fields;
        try
        { fields = ParseSimpleYaml(yaml); }
        catch (FormatException ex) { issues.Add(ex.Message); return issues; }

        if (!fields.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
            issues.Add("Frontmatter must have a 'name' field");
        else
        {
            if (!name.Equals(folderName, StringComparison.Ordinal))
                issues.Add($"name '{name}' must match folder name '{folderName}'");
            if (name.Contains("claude", StringComparison.OrdinalIgnoreCase))
                issues.Add("name must not contain 'claude'");
            if (name.Contains("anthropic", StringComparison.OrdinalIgnoreCase))
                issues.Add("name must not contain 'anthropic'");
        }

        if (!fields.TryGetValue("description", out var desc) || string.IsNullOrWhiteSpace(desc))
            issues.Add("Frontmatter must have a non-empty 'description' field");
        else
        {
            if (desc.Length > 1024)
                issues.Add($"Description must be ≤1024 chars, got {desc.Length}");
            if (ForbiddenCharsRegex().IsMatch(desc))
                issues.Add("Description must not contain '<' or '>'");
        }

        return issues;
    }

    /// <summary>
    /// Parse simple YAML key: value pairs. Handles:
    /// - key: value
    /// - key: > (multi-line folded)
    /// - key: (continuation lines indented)
    /// Rejects: nested maps, complex types.
    /// </summary>
    static Dictionary<string, string> ParseSimpleYaml(string yaml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = yaml.Split("\n");
        string? currentKey = null;
        var valueBuilder = new StringBuilder();
        var inFoldedBlock = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (string.IsNullOrEmpty(trimmed))
            {
                if (inFoldedBlock && currentKey is not null)
                    valueBuilder.Append(' ');
                continue;
            }

            // Check if this is a new key: value line
            if (trimmed.Length > 0 && !trimmed.StartsWith(' ') && !trimmed.StartsWith('-') && trimmed.Contains(':'))
            {
                // Flush previous key
                if (currentKey is not null)
                {
                    result[currentKey] = valueBuilder.ToString().Trim();
                    valueBuilder.Clear();
                }

                var colonIdx = trimmed.IndexOf(':');
                currentKey = trimmed[..colonIdx].Trim();
                var value = trimmed[(colonIdx + 1)..].Trim();

                // Check for folded block indicator
                if (value == ">")
                {
                    inFoldedBlock = true;
                    continue;
                }

                // Check for nested map (indented block under key:)
                if (string.IsNullOrEmpty(value))
                {
                    // Look ahead — if next line is indented, it's a continuation
                    inFoldedBlock = true;
                    continue;
                }

                valueBuilder.Append(value);
                inFoldedBlock = false;
            }
            else if (inFoldedBlock && currentKey is not null)
            {
                // Continuation line for multi-line value
                if (valueBuilder.Length > 0)
                    valueBuilder.Append(' ');
                valueBuilder.Append(trimmed);
            }
            else if (trimmed.StartsWith('-'))
            {
                // List item — we store as comma-separated under the current key
                // This is a simplified handling for skill frontmatter
                continue; // Skip list items for now
            }
            else
            {
                throw new FormatException(
                    $"Unsupported YAML construct at line {i + 1}: '{trimmed}'. " +
                    "Only simple key: value pairs and multi-line (> ) blocks are supported.");
            }
        }

        // Flush last key
        if (currentKey is not null)
            result[currentKey] = valueBuilder.ToString().Trim();

        return result;
    }
}

public sealed record SkillFrontmatter(
    string Name,
    string Description,
    string? License,
    string? Compatibility,
    Dictionary<string, string>? Metadata);
