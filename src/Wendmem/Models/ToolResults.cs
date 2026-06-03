using Wendmem.Experiences;
using Wendmem.Experiences.Extractors;

namespace Wendmem.Models;

public record AddMemoryResult(string? Id, string Wing, string? Room, bool Admitted = true, string? Reason = null, string? MatchedId = null);
public record DeleteMemoryResult(string Id, bool Deleted);
public record WingRoomEntry(string Wing, string Room);
public record SearchHit(string Id, string Wing, string Room, string Content, string? Source, float Score);
public record SearchMemoriesHit(string Id, string Wing, string Room, string Content, string? Source, string? Regime);
public record PruneResult(string Wing, float Threshold, int Clusters, int Retired, int Kept);
public record DrawerGrepHit(string Id, string Wing, string Room, string Content, string? Source);
public record ErrorResponse(string Error);
public record SaveSessionStateResult(string Id, string Wing, string Key, string Status);

public record AddEntityResult(string Id, string Name, string Type);
public record ConflictInfo(string TripleId, string Subject, string Predicate, string Object, DateOnly ValidFrom, string Message);
public record AddTripleResult(string Id, string Subject, string Predicate, string Obj, ConflictInfo? Conflict = null);
public record InvalidateResult(bool Invalidated, string Subject, string Predicate, string Obj);
public record QueryTripleEntry(string Subject, string Predicate, string Object, string ValidFrom, string? ValidTo, double Confidence);
public record KgStatsResult(long Entities, long Triples, long ActiveTriples);

public record MineFileResult(int FilesProcessed, int DrawersAdded, int FilesSkipped);
public record MineConvResult(int DrawersAdded, int DrawersSkipped);
public record SweepReportResult(int Missing, int Stale, int Ok, int Fixed, IReadOnlyList<string>? MissingFiles, IReadOnlyList<string>? StaleFiles);

public record AddTunnelResult(string Id, string Topic, string WingA, string RoomA, string WingB, string RoomB);
public record TunnelEntry(string Topic, string Wing, string Room);
public record TunnelPair(string WingA, string RoomA, string WingB, string RoomB);

public record SearchTaskMemoryEntry(string Id, string WhenToUse, string Content, float Score, float Similarity, string Source);
public record DistillResult(int Count, IReadOnlyList<string> Ids);
public record RecordOutcomeResult(int Recorded, string Outcome);
public record PruneTaskResult(int Deleted);
public record ExportTaskMemoryResult(int Exported, string Path);
public record ImportTaskMemoryResult(int Imported, string Wing);

public record RecordToolCallResult(string Id, bool Recorded);
public record SummarizeToolResult(bool Summarized, string? Reason, string? ToolName, float? Score, string? GuidelinesPreview);
public record ToolGuidelinesResult(bool Found, string? Guidelines, float? Score, string? Author);
public record ToolStatisticsResult(string ToolName, int TotalCalls, int Successes, float SuccessRate, double AvgTimeSeconds, int AvgTokenCost);
public record ToolCallEntry(string Id, string ToolName, bool Success, float Score, string? Summary, int TokenCost, double TimeSeconds, bool IsSummarized, DateTimeOffset CalledAt);

public static class RawMemoryDtoExt
{
    public static bool IsValid(RawMemoryDto r) =>
        !string.IsNullOrWhiteSpace(r.WhenToUse) &&
        !string.IsNullOrWhiteSpace(r.Experience ?? r.Content);

    public static ExtractedMemory ToExtracted(RawMemoryDto r, TaskMemorySource source) => new(
        WhenToUse: r.WhenToUse!.Trim(),
        Content: (r.Experience ?? r.Content)!.Trim(),
        Keywords: r.Keywords ?? r.Tags ?? [],
        Score: Math.Clamp(r.Score ?? r.Confidence ?? 0.8f, 0.0f, 1.0f),
        ToolsUsed: r.ToolsUsed ?? [],
        Source: source);
}

public record ExportLine(string MemoryId, string WorkspaceId, string WhenToUse, string Content, float Score, string Author);
public record WalEntry(string Timestamp, string Operation, Dictionary<string, string?> Params);
public record RawMemoryDto(string? WhenToUse, string? Experience, string? Content, string[]? Tags, string[]? Keywords, float? Confidence, float? Score, string[]? ToolsUsed);
public record ValidationVerdictDto(bool IsValid, float Score, string? Feedback);
public record CallVerdictDto(string? Summary, float Score);
public record EntityCandidate(string Name, string Type);
