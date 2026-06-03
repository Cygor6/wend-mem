namespace Wendmem.Wiki.Models;

public sealed record WikiPageDto(
    string Path, string Wing, string Title, string Content,
    IReadOnlyList<string> Citations, IReadOnlyList<string> Backlinks,
    long UpdatedAt, string? UpdatedBy);

public sealed record WikiPageHeaderDto(
    string Path, string Wing, string Title, long UpdatedAt, int CitationCount);

public sealed record WikiIndexDto(IReadOnlyList<WikiPageHeaderDto> Pages);

public sealed record WikiSearchHitDto(
    string Path, string Title, string Wing, long UpdatedAt);

public sealed record WikiSearchDto(IReadOnlyList<WikiSearchHitDto> Pages);

public sealed record BrokenCitationDto(string PagePath, string MissingDrawerId);

public sealed record WikiLintDto(
    IReadOnlyList<string> OrphanPages,
    IReadOnlyList<BrokenCitationDto> BrokenCitations,
    IReadOnlyList<string> StalePages);

public sealed record WikiLogEntryDto(
    string EventType, string? PagePath, long OccurredAt, string Summary);

public sealed record WikiLogDto(IReadOnlyList<WikiLogEntryDto> Entries);

public sealed record WikiWriteResultDto(string Path, string Result);

public sealed record WakeUpPageDto(string Path, string Title, long UpdatedAt);

public sealed record WakeUpActivityDto(
    string EventType, string? PagePath, long OccurredAt, string Summary);

public sealed record WakeUpMapDto(
    string? Wing,
    IReadOnlyList<WakeUpPageDto> WikiPages,
    IReadOnlyList<string> ActiveFacts,
    IReadOnlyList<WakeUpActivityDto> RecentActivity,
    IReadOnlyList<PendingUpdateSummaryDto> PendingUpdates,
    string Hint);

public sealed record WakeUpActivityTailDto(
    IReadOnlyList<WakeUpActivityDto> Activity,
    IReadOnlyList<WakeUpEpisodeDto>? Episodes = null,
    IReadOnlyList<WakeUpSkillDto>? Skills = null,
    IReadOnlyList<WakeUpReflectionDraftDto>? ReflectionDrafts = null);

public sealed record WakeUpEpisodeDto(string Id, string Goal, string Outcome, string? NextTime);

public sealed record WakeUpSkillDto(string Id, string Name, string Path, string Description, float SuccessRate);

public sealed record WakeUpReflectionDraftDto(string Id, string SuggestedPath, string SuggestedTitle, string Question);

public sealed record PendingUpdateSummaryDto(string PagePath, int CandidateCount);

// WikiMaintenanceTools DTOs (AOT-safe replacements for anonymous types)
public sealed record PendingUpdateDto(string PagePath, string DrawerId, float Similarity, long QueuedAt);
public sealed record PendingUpdatesResponseDto(IReadOnlyList<PendingUpdateDto> PendingUpdates);
public sealed record DismissResponseDto(bool Dismissed, string PagePath, string DrawerId);
public sealed record LintFindingDto(string Rule, string Severity, string PagePath, string Message, Dictionary<string, string?>? Details);
public sealed record LintWikiResponseDto(string? Wing, int PageCount, IReadOnlyList<LintFindingDto> Findings, Dictionary<string, int> CountsByRule);
public sealed record DistillCandidatePageDto(string? Path, IReadOnlyList<string>? PendingDrawerIds, string? DrawerId, float? Score);
public sealed record DistillScaffoldDto(string SuggestedPath, string SuggestedTitle, string DraftOutline);
public sealed record DistillResponseDto(string Wing, IReadOnlyList<DistillCandidatePageDto> CandidatePages, DistillScaffoldDto NewPageScaffold, string NextAction);

public sealed record GetDrawerDto(
    string Id, string Wing, string Room, string Content,
    string? Source, long MinedAt);
