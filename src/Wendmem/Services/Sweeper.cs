using Wendmem.Storage;

namespace Wendmem.Services;

record SweeperReport(
    int MissingCount,
    int StaleCount,
    int OkCount,
    int Fixed,
    IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string> StaleFiles
);

sealed class Sweeper(DrawerStorage storage, FileMiner miner)
{
    public async Task<SweeperReport> SweepAsync(
        string rootPath, string wing, bool fix, CancellationToken ct)
    {
        var missing = new List<string>();
        var stale = new List<string>();
        var ok = 0;

        foreach (var file in Directory
            .EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Where(f => !FileMiner.IsSkippedStatic(f)))
        {
            var mtime = new DateTimeOffset(
                File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds();
            var recorded = await storage.GetSourceMtimeAsync(file, ct);

            if (recorded is null)
                missing.Add(file);
            else if (recorded != mtime)
                stale.Add(file);
            else
                ok++;
        }

        int fixed_ = 0;
        if (fix)
        {
            foreach (var f in missing.Concat(stale))
            {
                var r = await miner.MineFileAsync(f, wing, "sweep", ct);
                fixed_ += r.DrawersAdded;
            }
            if (fixed_ > 0)
                await storage.RebuildFtsIndexAsync(ct);
        }

        return new SweeperReport(missing.Count, stale.Count, ok, fixed_,
                                 missing, stale);
    }
}
