using System.Text.Json;
using Wendmem.Storage;

namespace Wendmem.Services;

sealed class ConversationMiner(
    DrawerStorage storage,
    NumericFactExtractor factExtractor,
    Chunkers.TopicShiftChunker topicShiftChunker,
    PalaceConfig config,
    Wiki.PendingUpdateService? pendingUpdateService = null,
    Services.ActivityLog? activityLog = null)
{
    public async Task<(int DrawersAdded, int DrawersSkipped)> MineFileAsync(
        string filePath, string wing, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(filePath, ct);
        return await MineTextAsync(text, wing, filePath, ct);
    }

    public async Task<(int DrawersAdded, int DrawersSkipped)> MineTextAsync(
        string text, string wing, string? sourceUri, CancellationToken ct)
    {
        var turns = TryParseJsonTurns(text) ?? [text];

        int added = 0, skipped = 0;
        var newDrawerIds = new List<string>();
        foreach (var turn in turns)
        {
            if (string.IsNullOrWhiteSpace(turn))
                continue;

            var chunks = config.TopicShiftChunkingEnabled
                ? await topicShiftChunker.ChunkAsync(turn, ct)
                : TopicShiftChunker.Chunk(turn);

            foreach (var chunk in chunks)
            {
                var adm = await storage.AddDrawerAsync(chunk, wing, "conversation", sourceUri, null, ct: ct);
                if (!adm.Admitted)
                    continue;
                var id = adm.Id;
                _ = factExtractor.ExtractAsync(chunk, "conversation", sourceUri, DateTimeOffset.UtcNow, ct);
                added++;
                newDrawerIds.Add(id);
            }
        }

        if (added > 0)
            await storage.RebuildFtsIndexAsync(ct);

        if (newDrawerIds.Count > 0 && pendingUpdateService is not null)
            await pendingUpdateService.QueueAsync(newDrawerIds, wing, ct: ct);

        if (activityLog is not null)
            await activityLog.LogAsync("mine", wing, sourceUri, null,
                $"{added} conversation drawers added", ct);

        return (added, skipped);
    }

    static List<string>? TryParseJsonTurns(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            return [.. doc.RootElement.EnumerateArray()
                .Where(el => el.TryGetProperty("content", out _))
                .Select(el => el.GetProperty("content").GetString() ?? "")
                .Where(s => s.Length > 0)];
        }
        catch { return null; }
    }
}
