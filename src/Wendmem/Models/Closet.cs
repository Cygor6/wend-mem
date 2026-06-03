namespace Wendmem.Models;

record Closet(
    string Id,
    string DrawerId,
    string? Wing,
    string? Room,
    string? SourceFile,
    string AaakText,
    DateTimeOffset CreatedAt
);
