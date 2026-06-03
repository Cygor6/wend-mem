namespace Wendmem.Services;

/// <summary>
/// Infers a room name from a file path.
/// Caller-supplied room always wins — only call this when room is null or empty.
/// </summary>
internal static class RoomClassifier
{
    public static string Classify(string filePath)
    {
        // Normalise to forward slashes, lower-case for matching
        var p = filePath.Replace('\\', '/').ToLowerInvariant();

        return p switch
        {
            _ when p.Contains("/controllers/") => "controllers",
            _ when p.Contains("/controller/") => "controllers",
            _ when p.Contains("/services/") => "services",
            _ when p.Contains("/service/") => "services",
            _ when p.Contains("/models/") => "models",
            _ when p.Contains("/model/") => "models",
            _ when p.Contains("/migrations/") => "migrations",
            _ when p.Contains("/migration/") => "migrations",
            _ when p.Contains("/helpers/") => "helpers",
            _ when p.Contains("/helper/") => "helpers",
            _ when p.Contains("/repositories/") => "repositories",
            _ when p.Contains("/repository/") => "repositories",
            _ when p.Contains("/handlers/") => "handlers",
            _ when p.Contains("/middleware/") => "middleware",
            _ when p.Contains("/tests/") => "tests",
            _ when p.Contains(".test.") => "tests",
            _ when p.Contains(".spec.") => "tests",
            _ when p.Contains("/specs/") => "tests",
            _ => "source"
        };
    }
}
