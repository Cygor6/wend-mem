namespace Wendmem.Wiki.Models;

public sealed record WikiPage(
    string Path,
    string Wing,
    string Title,
    string Content,
    IReadOnlyList<string> Citations,
    IReadOnlyList<string> Backlinks,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);
