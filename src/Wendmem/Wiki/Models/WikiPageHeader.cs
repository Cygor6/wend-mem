namespace Wendmem.Wiki.Models;

public sealed record WikiPageHeader(
    string Path,
    string Wing,
    string Title,
    DateTimeOffset UpdatedAt,
    int CitationCount,
    float Score = 0f);
