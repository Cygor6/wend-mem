namespace Wendmem.Models;

record Triple(
    string Subject,
    string Predicate,
    string Object,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    double Confidence,
    string? SourceRoom,
    string? SourceFile
);

record KgStats(long EntityCount, long TripleCount, long ActiveTripleCount);

enum TripleDirection { Outgoing, Incoming, Both }
