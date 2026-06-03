namespace Wendmem.Wiki.Models;

public sealed record WikiLogEntry(
    string Id,
    string Wing,
    string EventType,
    string? PagePath,
    string Summary,
    DateTimeOffset OccurredAt);
