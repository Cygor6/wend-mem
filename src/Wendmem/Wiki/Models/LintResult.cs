namespace Wendmem.Wiki.Models;

public sealed record LintResult(
    IReadOnlyList<string> OrphanPages,
    IReadOnlyList<BrokenCitation> BrokenCitations,
    IReadOnlyList<string> StalePages);
