namespace Wendmem.Services;

/// <summary>
/// Semantic room routing ("Halls"). When add_memory is called without an explicit room,
/// the content is scored against a configurable keyword map to auto-detect the best room.
///
/// Matching rules (bilingual SV/EN aware):
///  - Keywords of length >= 4 are WORD-START anchored: they match at the start of a
///    word but may continue into it. This catches Swedish inflection and compounds
///    where the keyword is the first element ("databas" → "databasen",
///    "databasfrågan"; "konfig" → "konfigurera") without suffix false positives
///    ("test" no longer matches "latest", "ui" no longer matches "build").
///  - Keywords of length <= 3 are matched as WHOLE WORDS ("ci" matches "ci/cd"
///    but not "circle"; "ini" not "initialize").
///  - A leading '=' forces whole-word matching for longer keywords
///    ("=rest" matches "REST" but not "restart"/"restore"). The '=' prefix works
///    in appsettings.json too.
///  - Swedish compounds with the keyword as LAST element ("enhetstest") are not
///    caught by word-start anchoring — add them as explicit keywords instead.
/// </summary>
sealed class HallDetector
{
    /// <summary>
    /// Map of hall (room) name to keyword list. Loaded from appsettings.json "Halls" section.
    /// Example: { "security": ["auth","token","login","lösenord","jwt","certifikat"] }
    /// </summary>
    readonly Dictionary<string, string[]> _halls;

    /// <summary>Pre-lowered, deduplicated keywords with their matching mode.</summary>
    readonly List<(string Hall, (string Keyword, bool WholeWord)[] Keywords)> _compiled;

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

        _compiled = _halls
            .Select(kv => (kv.Key, Compile(kv.Value)))
            .Where(t => t.Item2.Length > 0)
            .ToList();
    }

    void LoadDefaults()
    {
        // Order matters: on equal scores the first hall wins, so security first.
        _halls["security"] = [
            "=auth", "authent", "authoriz", "authoris", "token", "login",
            "password", "jwt", "oauth", "certificate", "encryption", "ssl",
            "tls", "credential", "secret", "vulnerability", "cve",
            "inloggning", "lösenord", "behörighet", "certifikat",
            "kryptering", "autentisering", "sårbarhet", "hemlig",
        ];
        _halls["config"] = [
            "config", "settings", "environment", "env", "variable", "dotenv",
            "appsettings", "yaml", "toml", "ini",
            "konfig", "inställning", "miljövariab",
        ];
        _halls["database"] = [
            "database", "sql", "query", "schema", "migration", "table",
            "index", "duckdb", "sqlite", "postgres", "sqlserver",
            "databas", "tabell", "kolumn", "migrering", "transaktion",
        ];
        _halls["api"] = [
            "api", "endpoint", "route", "handler", "controller", "request",
            "response", "http", "=rest", "graphql", "grpc",
            "anrop", "slutpunkt", "förfrågan",
        ];
        _halls["integration"] = [
            "integration", "messaging", "rabbitmq", "masstransit", "kafka",
            "queue", "webhook", "edi", "edifact", "sftp", "service bus",
            "servicebus", "meddelande", "kö", "synk",
        ];
        _halls["data"] = [
            "databricks", "dbt", "power bi", "powerbi", "ducklake",
            "lakehouse", "datalager", "etl", "kpi", "dashboard", "parquet",
            "nyckeltal", "rapport",
        ];
        _halls["ui"] = [
            "ui", "frontend", "component", "css", "html", "render", "layout",
            "button", "page",
            "gränssnitt", "komponent", "knapp", "formulär", "sida",
        ];
        _halls["testing"] = [
            "test", "spec", "assert", "mock", "fixture", "coverage",
            "benchmark", "smoke",
            "enhetstest", "integrationstest", "täckning",
        ];
        _halls["docs"] = [
            "readme", "documentation", "guide", "tutorial", "example",
            "usage", "install",
            "dokumentation", "handbok", "exempel", "instruktion", "manual",
            "beskrivning",
        ];
        _halls["devops"] = [
            "docker", "deploy", "ci", "cd", "cicd", "pipeline", "build",
            "container", "kubernetes", "helm",
            "driftsätt", "octopus", "terraform", "ansible", "kluster",
            "release", "github actions",
        ];
        _halls["general"] = [];
    }

    static (string Keyword, bool WholeWord)[] Compile(string[] keywords)
        => keywords
            .Select(k => k.Trim().ToLowerInvariant())
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(k => k[0] == '='
                ? (Keyword: k[1..], WholeWord: true)
                : (Keyword: k, WholeWord: k.Length <= 3))
            .Where(t => t.Keyword.Length > 0)
            .ToArray();

    /// <summary>
    /// Returns all hall-to-keyword mappings for schema documentation.
    /// Keywords are as configured ('=' whole-word markers included).
    /// </summary>
    public IReadOnlyDictionary<string, string[]> GetAllMappings() => _halls;

    /// <summary>
    /// Detect the best hall (room) for the given content.
    /// Returns the hall with the highest keyword hit count, or "general" if no keywords match.
    /// </summary>
    public string Detect(ReadOnlySpan<char> content)
    {
        if (_compiled.Count == 0)
            return "general";

        // Only scan first 5000 chars for performance
        var scanLen = Math.Min(content.Length, 5000);
        var contentLower = content[..scanLen].ToString().ToLowerInvariant();

        string bestHall = "general";
        int bestScore = 0;

        foreach (var (hall, keywords) in _compiled)
        {
            int score = 0;
            foreach (var (keyword, wholeWord) in keywords)
                score += CountMatches(contentLower, keyword, wholeWord);

            if (score > bestScore)
            {
                bestScore = score;
                bestHall = hall;
            }
        }

        return bestHall;
    }

    /// <summary>
    /// Counts boundary-respecting occurrences of <paramref name="keyword"/>:
    /// the match must start at a word boundary, and for whole-word keywords
    /// it must also end at one. Boundary = start/end of text or a character
    /// that is not a letter or digit (so "ci" matches in "ci/cd" and
    /// "kpi" in "kpi:er").
    /// </summary>
    static int CountMatches(string contentLower, string keyword, bool wholeWord)
    {
        int count = 0;
        int idx = 0;

        while ((idx = contentLower.IndexOf(keyword, idx, StringComparison.Ordinal)) >= 0)
        {
            bool startOk = idx == 0 || !char.IsLetterOrDigit(contentLower[idx - 1]);
            int end = idx + keyword.Length;
            bool endOk = !wholeWord || end >= contentLower.Length || !char.IsLetterOrDigit(contentLower[end]);

            if (startOk && endOk)
            {
                count++;
                idx = end;
            }
            else
            {
                idx++;
            }
        }

        return count;
    }
}
