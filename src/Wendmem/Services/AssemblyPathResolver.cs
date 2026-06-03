namespace Wendmem.Services;

/// <summary>
/// Resolves the base directory for model files.
/// Handles two scenarios:
///   1. Published AOT binary: models are siblings of the executable.
///   2. Dev (dotnet run): assembly is in bin/Debug/net10.0/ but models
///      are at the project root. Walks up to find them.
/// </summary>
public static class AssemblyPathResolver
{
    private static readonly Lazy<string> _basePath = new(FindBasePath);

    /// <summary>
    /// Directory containing model files. Resolved once at first access.
    /// </summary>
    public static string BasePath => _basePath.Value;

    static string FindBasePath()
    {
        // AppContext.BaseDirectory works for both published AOT binaries and
        // dev runs (dotnet run). Assembly.Location returns empty in single-file
        // AOT apps and triggers IL3000, so we avoid it entirely.
        var startDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Check if models are right next to the assembly (published scenario)
        if (HasModelsDir(startDir))
            return startDir;

        // Walk up looking for models/ (dev scenario: bin/Debug/net10.0 -> project root)
        var dir = startDir;
        for (var i = 0; i < 6; i++)
        {
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null)
                break;
            if (HasModelsDir(parent))
                return parent;
            dir = parent;
        }

        // Give up — return the assembly dir. ModelValidator will report
        // the missing files with actionable paths.
        return startDir;
    }

    static bool HasModelsDir(string dir)
    {
        return Directory.Exists(Path.Combine(dir, "models", "embeddinggemma"));
    }
}
