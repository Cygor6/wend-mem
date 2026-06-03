using System.Text.RegularExpressions;

namespace Wendmem.Wiki;

/// <summary>
/// Input validation and normalization for all MCP tool string parameters.
/// Centralizes ASCII/kebab-case checks and drawer-ID hex checks.
/// Automatically slugifies wing/room/path values so callers passing
/// uppercase, underscores, or spaces get normalized instead of rejected.
/// All Validate* methods return the normalized string for downstream use.
/// </summary>
internal static partial class PathValidator
{
    [GeneratedRegex(@"^[a-z][a-z0-9-]*(/[a-z][a-z0-9-]*)*$")]
    private static partial Regex KebabRegex();

    [GeneratedRegex(@"^[0-9a-f]{16}$")]
    public static partial Regex DrawerIdRegex();

    [GeneratedRegex(@"_+")]
    private static partial Regex UnderscoreRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"-+")]
    private static partial Regex MultiHyphenRegex();

    /// <summary>
    /// Normalize a free-form string into kebab-case ASCII.
    /// Handles: uppercase → lowercase, underscores → hyphens, spaces → hyphens,
    /// consecutive hyphens collapsed, non-ASCII stripped, leading/trailing hyphens trimmed.
    /// Returns null if the result is empty after normalization.
    /// </summary>
    public static string? Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // Strip non-printable-ASCII, lowercase, replace separators
        var s = TrimNonAscii(value).ToLowerInvariant();
        s = UnderscoreRegex().Replace(s, "-");
        s = WhitespaceRegex().Replace(s, "-");
        // Remove any character that isn't a-z, 0-9, hyphen, or slash
        var chars = new char[s.Length];
        int j = 0;
        foreach (var c in s)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '/')
                chars[j++] = c;
        }
        s = new string(chars, 0, j);
        // Collapse consecutive hyphens
        s = MultiHyphenRegex().Replace(s, "-");
        // Trim leading/trailing hyphens per segment (split by /)
        var segments = s.Split('/');
        for (int i = 0; i < segments.Length; i++)
            segments[i] = segments[i].Trim('-');
        s = string.Join("/", segments.Where(seg => seg.Length > 0));
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>
    /// Validate and normalize a wiki page path or wing/room namespace identifier.
    /// Returns the slugified form for use in downstream queries.
    /// </summary>
    public static string ValidatePath(string path)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(path);
        var slug = Slugify(path);
        if (slug is null || slug.Length > 100)
            throw new ArgumentException(
                slug is null
                    ? $"Path '{path}' could not be normalized to kebab-case."
                    : "Path exceeds 100 characters.",
                nameof(path));
        return slug;
    }

    /// <summary>
    /// Returns the normalized wing slug. If <paramref name="wing"/> is null or
    /// whitespace, falls back to <paramref name="config"/>.DefaultWing.
    /// When <paramref name="config"/>.ForceDefaultWing is true, always uses
    /// DefaultWing regardless of the caller-supplied value.
    /// </summary>
    public static string ResolveWing(string? wing, PalaceConfig config)
    {
        if (config.ForceDefaultWing || string.IsNullOrWhiteSpace(wing))
            return ValidateWing(config.DefaultWing);
        return ValidateWing(wing);
    }

    /// <summary>
    /// Validate and normalize a wing namespace. Returns the slugified form.
    /// </summary>
    public static string ValidateWing(string wing)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(wing);
        var slug = Slugify(wing);
        if (slug is null)
            throw new ArgumentException(
                $"Wing '{wing}' could not be normalized to kebab-case.",
                nameof(wing));
        return slug;
    }

    /// <summary>
    /// Validate and normalize an optional wing.
    /// Returns null when input is null/empty, otherwise returns the slugified form.
    /// </summary>
    public static string? ValidateOptionalWing(string? wing)
    {
        if (string.IsNullOrWhiteSpace(wing))
            return null;
        return ValidateWing(wing);
    }

    /// <summary>
    /// Validate and normalize an optional room.
    /// Returns null when input is null/empty, otherwise returns the slugified form.
    /// </summary>
    public static string? ValidateOptionalRoom(string? room)
    {
        if (string.IsNullOrWhiteSpace(room))
            return null;
        return ValidateRoom(room);
    }

    /// <summary>
    /// Validate and normalize a room identifier. Returns the slugified form.
    /// </summary>
    public static string ValidateRoom(string room)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(room);
        var slug = Slugify(room);
        if (slug is null)
            throw new ArgumentException(
                $"Room '{room}' could not be normalized to kebab-case.",
                nameof(room));
        return slug;
    }

    /// <summary>
    /// Validate a drawer ID — must be exactly 16 lowercase hex characters.
    /// Returns the trimmed, validated ID.
    /// </summary>
    public static string ValidateDrawerId(string id)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(id);
        var trimmed = id.Trim();
        if (!DrawerIdRegex().IsMatch(trimmed))
            throw new ArgumentException(
                $"Invalid drawer ID '{id}'. Expected 16-char hex (e.g. 696d52e528f355da) " +
                "from AddMemory/SearchMemories response. Not a file path.",
                nameof(id));
        return trimmed;
    }

    // Strip leading/trailing characters outside printable ASCII (U+0020–U+007E).
    static string TrimNonAscii(string value)
    {
        var span = value.AsSpan();
        int start = 0;
        while (start < span.Length && (span[start] < 0x20 || span[start] > 0x7E))
            start++;
        int end = span.Length;
        while (end > start && (span[end - 1] < 0x20 || span[end - 1] > 0x7E))
            end--;
        return start == 0 && end == span.Length ? value : span[start..end].ToString();
    }
}
