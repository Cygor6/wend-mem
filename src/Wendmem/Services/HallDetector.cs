namespace Wendmem.Services;

/// <summary>
/// Semantic room routing ("Halls"). When add_memory is called without an explicit room,
/// the content is scored against a configurable keyword map to auto-detect the best room.
/// </summary>
sealed class HallDetector
{
    /// <summary>
    /// Map of hall (room) name to keyword list. Loaded from appsettings.json "Halls" section.
    /// Example: { "security": ["auth","token","login","password","jwt","oauth","certificate"] }
    /// </summary>
    readonly Dictionary<string, string[]> _halls;

    public HallDetector(Dictionary<string, string[]> halls)
    {
        _halls = new(StringComparer.OrdinalIgnoreCase);

        foreach (var (hall, keywords) in halls)
        {
            if (keywords is { Length: > 0 })
                _halls[hall] = keywords;
        }

        if (_halls.Count == 0)
            LoadDefaults();
    }

    void LoadDefaults()
    {
        _halls["security"] = ["auth", "token", "login", "password", "jwt", "oauth", "certificate", "encryption", "ssl", "tls", "credential", "secret", "vulnerability", "cve"];
        _halls["config"] = ["config", "settings", "environment", "env", "variable", "dotenv", "appsettings", "yaml", "toml", "ini"];
        _halls["database"] = ["database", "sql", "query", "schema", "migration", "table", "index", "duckdb", "sqlite", "postgres"];
        _halls["api"] = ["api", "endpoint", "route", "handler", "controller", "request", "response", "http", "rest", "graphql"];
        _halls["ui"] = ["ui", "frontend", "component", "css", "html", "render", "layout", "button", "form", "page"];
        _halls["testing"] = ["test", "spec", "assert", "mock", "fixture", "coverage", "benchmark", "smoke"];
        _halls["docs"] = ["readme", "documentation", "guide", "tutorial", "example", "usage", "install"];
        _halls["devops"] = ["docker", "deploy", "ci", "cd", "pipeline", "build", "container", "kubernetes", "helm"];
        _halls["general"] = [];
    }

    /// <summary>
    /// Returns all hall-to-keyword mappings for schema documentation.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> GetAllMappings() => _halls;

    /// <summary>
    /// Detect the best hall (room) for the given content.
    /// Returns the hall with the highest keyword hit count, or "general" if no keywords match.
    /// </summary>
    public string Detect(ReadOnlySpan<char> content)
    {
        if (_halls.Count == 0)
            return "general";

        // Only scan first 5000 chars for performance
        var scanLen = Math.Min(content.Length, 5000);
        var contentLower = content.Slice(0, scanLen).ToString().ToLowerInvariant();

        string bestHall = "general";
        int bestScore = 0;

        foreach (var (hall, keywords) in _halls)
        {
            if (keywords.Length == 0)
                continue;

            int score = 0;
            foreach (var kw in keywords)
            {
                var kwLower = kw.ToLowerInvariant();
                var idx = 0;
                while ((idx = contentLower.IndexOf(kwLower, idx, StringComparison.Ordinal)) >= 0)
                {
                    score++;
                    idx += kwLower.Length;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestHall = hall;
            }
        }

        return bestHall;
    }
}
