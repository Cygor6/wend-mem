using System.Security.Cryptography;
using System.Text;
using DuckDB.NET.Data;
using Wendmem.Storage;

namespace Wendmem.Services;

sealed class FileMiner(
    DrawerStorage storage,
    DuckDbConnectionFactory dbFactory,
    NumericFactExtractor factExtractor,
    Chunkers.TopicShiftChunker topicShiftChunker,
    PalaceConfig config,
    Wiki.PendingUpdateService? pendingUpdateService = null,
    Services.ActivityLog? activityLog = null)
{
    static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".pdb",
        ".nupkg", ".snupkg",
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".svg", ".bmp", ".tiff", ".webp",
        ".woff", ".woff2", ".ttf", ".eot", ".otf",
        ".zip", ".tar", ".gz", ".rar", ".7z", ".bz2",
        ".pdf", ".docx", ".xlsx", ".pptx",
        ".db", ".duckdb", ".sqlite", ".mdb",
        ".onnx", ".pt", ".bin", ".h5", ".safetensors",
        ".resources",
        ".suo", ".user", ".cache",
        ".vspscc", ".vssscc",
        ".pfx", ".p12", ".pem", ".key", ".snk",
        ".mp3", ".mp4", ".wav", ".avi", ".mov", ".mkv",
        ".min.js", ".min.css",
    };

    static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", ".idea", ".vscode",
        "node_modules", ".nuget", ".dotnet",
        ".git", ".svn",
        "TestResults", "test-results",
        "__pycache__", ".venv", "venv",
        "target", ".gradle", "__build",
        "publish", "out",
        "dist", "build", ".next", ".nuxt",
        "coverage", ".cache",
    };

    static readonly HashSet<string> SkipFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "yarn.lock", "Cargo.lock",
        "poetry.lock", "Gemfile.lock", "pnpm-lock.yaml",
        "navigationconfig.g.cs",
    };

    static readonly string[] SkipFileNameSuffixes =
    [
        ".AssemblyInfo.cs", ".AssemblyAttributes.cs", ".GlobalUsings.g.cs",
        ".g.cs", ".g.i.cs", ".generated.cs",
        ".Designer.cs",
        ".min.js", ".min.css",
    ];

    public async Task<(int FilesProcessed, int DrawersAdded, int FilesSkipped)>
        MineDirectoryAsync(string rootPath, string wing, string? room, CancellationToken ct)
    {
        if (!Directory.Exists(rootPath))
        {
            Console.Error.WriteLine($"Skipped directory '{rootPath}': directory not found.");
            return (0, 0, 0);
        }

        int processed = 0, added = 0, skipped = 0;
        var newDrawerIds = new List<string>();

        IEnumerable<string> files;
        try
        { files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to enumerate '{rootPath}': {ex.GetType().Name} — {ex.Message}");
            return (0, 0, 0);
        }

        foreach (var file in files.Where(f => !IsSkipped(f)))
        {
            var fileRoom = !string.IsNullOrWhiteSpace(room)
                ? room
                : RoomClassifier.Classify(file);
            var r = await MineFileAsync(file, wing, fileRoom, ct);
            processed += r.FilesProcessed;
            added += r.DrawersAdded;
            skipped += r.FilesSkipped;
            newDrawerIds.AddRange(r.NewDrawerIds);
        }

        if (added > 0)
            await storage.RebuildFtsIndexAsync(ct);

        if (newDrawerIds.Count > 0 && pendingUpdateService is not null)
            await pendingUpdateService.QueueAsync(newDrawerIds, wing, ct: ct);

        if (activityLog is not null)
            await activityLog.LogAsync("mine", wing, rootPath, null,
                $"{processed} files, {added} drawers added", ct);

        return (processed, added, skipped);
    }

    public async Task<(int FilesProcessed, int DrawersAdded, int FilesSkipped, List<string> NewDrawerIds)>
        MineFileAsync(string filePath, string wing, string? room, CancellationToken ct)
    {
        if (IsSkipped(filePath))
            return (0, 0, 1, []);

        string text;
        try
        { text = await File.ReadAllTextAsync(filePath, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Skipped '{filePath}': {ex.GetType().Name} — {ex.Message}");
            return (0, 0, 1, []);
        }

        if (string.IsNullOrWhiteSpace(text))
            return (1, 0, 0, []);

        long mtime;
        try
        { mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(filePath)).ToUnixTimeMilliseconds(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Skipped '{filePath}': {ex.GetType().Name} — {ex.Message}");
            return (0, 0, 1, []);
        }
        var existing = await storage.GetSourceMtimeAsync(filePath, ct);
        if (existing == mtime)
            return (1, 0, 0, []);

        room = string.IsNullOrWhiteSpace(room)
            ? RoomClassifier.Classify(filePath)
            : room;

        bool wasFallback = room == "source";
        await dbFactory.ExecuteWriteAsync(async db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO room_classification_log
                    (id, source_file, extension, directory, assigned_room, was_fallback)
                VALUES ($id, $file, $ext, $dir, $room, $fallback)
                ON CONFLICT DO NOTHING
                """;
            cmd.Parameters.Add(new DuckDBParameter("id", ShortHash(filePath + room)));
            cmd.Parameters.Add(new DuckDBParameter("file", filePath));
            cmd.Parameters.Add(new DuckDBParameter("ext", Path.GetExtension(filePath).ToLowerInvariant()));
            cmd.Parameters.Add(new DuckDBParameter("dir", Path.GetDirectoryName(filePath) ?? ""));
            cmd.Parameters.Add(new DuckDBParameter("room", room));
            cmd.Parameters.Add(new DuckDBParameter("fallback", wasFallback));
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

        int added = 0;
        var newIds = new List<string>();
        var chunks = await topicShiftChunker.ChunkAsync(text, ct);

        foreach (var chunk in chunks)
        {
            var adm = await storage.AddDrawerAsync(chunk, wing, room, filePath, mtime, ct: ct);
            if (!adm.Admitted)
                continue;
            var id = adm.Id;
            _ = factExtractor.ExtractAsync(chunk, room, filePath, DateTimeOffset.UtcNow, ct);
            added++;
            newIds.Add(id);
        }

        if (added > 0)
            await storage.RebuildFtsIndexAsync(ct);

        return (1, added, 0, newIds);
    }
    bool IsSkipped(string path)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path);
        if (SkipFiles.Contains(name))
            return true;
        if (SkipExtensions.Contains(ext))
            return true;
        if (path.Split(Path.DirectorySeparatorChar)
                .Any(seg => SkipDirectories.Contains(seg)))
            return true;
        return SkipFileNameSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSkippedStatic(string path)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path);
        if (SkipFiles.Contains(name))
            return true;
        if (SkipExtensions.Contains(ext))
            return true;
        if (path.Split(Path.DirectorySeparatorChar)
                .Any(seg => SkipDirectories.Contains(seg)))
            return true;
        return SkipFileNameSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    static string InferRoom(string file, string root)
    {
        var rel = Path.GetRelativePath(root, Path.GetDirectoryName(file) ?? root);
        return rel == "." ? "root" : rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    static string ShortHash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
              .ToLowerInvariant()[..16];
}
