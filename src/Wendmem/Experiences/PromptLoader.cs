namespace Wendmem.Experiences;

internal static class PromptLoader
{
    static readonly Dictionary<string, string> Cache = new();
    static readonly object Lock = new();

    public static async Task<string> LoadAsync(string fileName, CancellationToken ct)
    {
        lock (Lock)
            if (Cache.TryGetValue(fileName, out var cached))
                return cached;

        var asm = typeof(PromptLoader).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded resource not found: {fileName}");

        await using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(ct);

        lock (Lock)
            Cache[fileName] = content;
        return content;
    }
}
