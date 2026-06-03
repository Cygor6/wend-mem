using System.Text.RegularExpressions;

namespace Wendmem.Services;

public sealed record AaakMetadata(
    string? SourceFile = null,
    string? Wing = null,
    string? Room = null,
    DateOnly? Date = null
);

public sealed partial class AaakDialect(Dictionary<string, string>? entityCodes = null)
{
    // Unicode-aware: \p{Lu} = any uppercase letter, \p{L} = any letter.
    // Covers å/ä/ö (and stray loanword diacritics) without an ASCII-only class.
    [GeneratedRegex(@"\b\p{Lu}\p{L}+\b")]
    private static partial Regex EntityRegex();

    // \p{Ll} = any lowercase letter, \p{Nd} = any decimal digit. Min length 4.
    [GeneratedRegex(@"\b\p{Ll}[\p{Ll}\p{Nd}_]{3,}\b")]
    private static partial Regex TopicRegex();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceSplitRegex();

    // ── Detection vocabularies (bilingual SV/EN) ───────────────────────────
    // All matching is substring `Contains` over lowercased text, so stems are
    // chosen to be distinctive. Avoid bare stems that hide inside common words:
    //   arg ⊂ argument · sur ⊂ resurs · tack ⊂ kontakt · less ⊂ regardless
    //   nöjd ⊂ missnöjd (meaning inversion!)
    // Swedish stems are truncated to catch inflection (orolig→oroligt/oroliga).

    // Affective signals → emotion code. One entry per code (Take(3) in Compress
    // picks the first three in table order, so order = priority).
    static readonly (string Code, string[] Signals)[] Emotions =
    [
        ("determ",   ["decided", "committed", "resolved", "determined",
                      "beslut", "bestämd", "fast besluten"]),
        ("convict",  ["prefer", "believe", "convinced",
                      "föredr", "tror", "övertygad"]),
        ("anx",      ["worried", "anxious", "concern", "nervous", "stressed",
                      "orolig", "oroad", "bekymr", "nervös", "stressad", "ängsl"]),
        ("excite",   ["excited", "thrilled", "pumped", "can't wait",
                      "exalter", "taggad", "peppad", "ser fram emot"]),
        ("frust",    ["frustrated", "annoyed", "fed up",
                      "irriter", "frustrer", "trött på", "less på"]),
        ("confuse",  ["confused", "unclear", "puzzled", "no idea",
                      "förvirr", "oklar", "förstår inte", "ingen aning", "vilse"]),
        ("love",     ["love", "adore", "älskar", "avgudar", "gillar verkligen"]),
        ("rage",     ["hate", "furious", "angry", "pissed",
                      "hatar", "rasande", "förbannad", "ursinnig", "ilska"]),
        ("hope",     ["hope", "hopeful", "hoppas", "förhoppning", "håller tummarna"]),
        ("fear",     ["fear", "scared", "afraid", "terrified", "dread",
                      "rädd", "rädsla", "skräck", "fruktar", "livrädd"]),
        ("joy",      ["happy", "glad", "pleased", "delighted",
                      "lycklig", "förtjust"]),
        ("grief",    ["sad", "disappointed",
                      "ledsen", "besviken", "nedstämd", "sorgsen"]),
        ("surprise", ["surprised", "shocked", "unexpected", "didn't expect",
                      "förvån", "överrask", "chock", "oväntad"]),
        ("grat",     ["grateful", "thankful", "appreciate",
                      "tacksam", "uppskatt", "tack för"]),
        ("curious",  ["curious", "intrigued", "interested",
                      "nyfiken", "undrar", "funderar på", "intresserad"]),
        ("relief",   ["relieved", "phew", "finally",
                      "lättad", "äntligen", "skönt"]),
        ("doubt",    ["doubt", "skeptical", "uncertain", "hesitant", "not sure",
                      "tveksam", "osäker", "skeptisk", "tvivlar", "tvekar"]),
    ];

    // Categorical flags. Binary presence — order = emission order.
    static readonly (string Flag, string[] Stems)[] Flags =
    [
        ("DECISION",  ["decided", "decision", "chose", "agreed", "concluded",
                       "opted", "settled", "resolved",
                       "beslut", "bestämd", "valde", "enades", "slutsats",
                       "kom överens", "avgjorde"]),
        ("TECHNICAL", ["architecture", "implementation", "api", "duckdb", "c#",
                       ".net", "database", "function", "method", "query",
                       "algorithm", "refactor", "deploy", "endpoint", "index",
                       "arkitektur", "implementer", "databas", "funktion",
                       "metod", "schema", "algoritm", "refaktor", "driftsätt"]),
        ("ORIGIN",    ["origin", "first time", "originally", "initially",
                       "at first", "ursprung", "ursprungligen", "från början",
                       "inledningsvis", "första gången"]),
        ("CORE",      ["core belief", "foundational", "fundamental", "principle",
                       "philosophy", "non-negotiable", "grundläggande",
                       "kärnövertygelse", "princip", "grundprincip", "filosofi"]),
        ("SENSITIVE", ["sensitive", "private", "confidential", "secret",
                       "password", "credential", "api key", "känslig", "privat",
                       "personlig", "hemlig", "konfidentiell", "lösenord",
                       "api-nyckel"]),
        ("PIVOT",     ["pivot", "turning point", "abandoned", "scrapped",
                       "rewrote", "switched to", "reconsidered", "vändpunkt",
                       "vändning", "övergav", "skrotade", "skrev om", "tänkte om",
                       "kursändring", "i stället"]),
        ("GENESIS",   ["genesis", "inception", "conceived", "born out of",
                       "begynnelse", "uppkomst", "tillkomst", "grundtanken",
                       "föddes ur"]),
    ];

    // Sentence-salience cues. Additive: every group that matches adds its weight,
    // so architecture and implementation stay separate to preserve summing.
    // Ubiquitous connectives (men/but/och, för att) are deliberately excluded —
    // they would flatten a ranker whose base score is sentence length.
    static readonly (int Weight, string[] Stems)[] ScoreCues =
    [
        (50, ["decided", "chose", "concluded", "agreed", "resolved", "opted",
              "beslut", "bestämd", "valde", "väljer", "enades", "kom fram till",
              "slutsats", "fastställ"]),
        (30, ["because", "since", "therefore", "rationale",
              "eftersom", "därför", "anledning", "skäl", "innebär att"]),
        (25, ["important", "critical", "essential", "crucial", "required",
              "viktig", "kritisk", "avgörande", "central", "måste", "krävs",
              "observera"]),
        (20, ["architecture", "arkitektur"]),
        (20, ["implementation", "implementer"]),
        (20, ["caveat", "gotcha", "deprecated", "broken", "fails",
              "varning", "begränsning", "trasig", "fallgrop", "föråldrad",
              "fungerar inte"]),
    ];

    // Capitalized function words that must not be mistaken for entities.
    static readonly HashSet<string> StopWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // English
            "The", "This", "That", "Then", "When", "Where", "What", "Why",
            "How", "And", "But", "For", "With", "From", "Into", "About",
            // Swedish
            "Den", "Det", "De", "Denna", "Detta", "Dessa", "När", "Var",
            "Vart", "Vad", "Varför", "Hur", "Och", "Men", "För", "Med",
            "Från", "Till", "Om", "Som", "Att", "En", "Ett", "Här", "Där",
        };

    // Lowercase words excluded from topic extraction. Only 4+ chars matter —
    // TopicRegex requires a minimum length of 4, so 3-char Swedish particles
    // (och, att, som, för...) are never matched in the first place.
    static readonly HashSet<string> CommonWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // English
            "this", "that", "with", "from", "have", "will", "would",
            "there", "their", "about", "because", "should", "could",
            "into", "when", "where", "what", "using", "used", "uses",
            "then", "than", "they", "them", "your", "our", "the",
            // Swedish
            "eller", "eftersom", "skulle", "kunde", "kunna", "deras",
            "denna", "detta", "dessa", "borde", "använder", "använda",
            "använt", "sedan", "vara", "vore", "blev", "blir", "genom",
            "över", "under", "mellan", "också", "bara", "inte", "vill",
            "ville", "hade", "skall", "samt", "alltså", "därför", "måste",
            "behöver", "finns", "gäller", "alla", "varje",
        };

    readonly Dictionary<string, string> _codes =
        entityCodes ?? new(StringComparer.OrdinalIgnoreCase);

    public string Compress(string text, AaakMetadata? meta = null)
    {
        meta ??= new AaakMetadata();

        var entities = DetectEntities(text).Take(3).ToArray();
        var topics = ExtractTopics(text).Take(3).ToArray();
        var quote = ExtractKeySentence(text);
        var emotions = DetectEmotions(text).Take(3).ToArray();
        var flags = DetectFlags(text).ToArray();

        var lines = new List<string>();

        if (meta.Wing is not null || meta.SourceFile is not null)
        {
            var stem = meta.SourceFile is not null
                ? Path.GetFileNameWithoutExtension(meta.SourceFile)
                : "?";
            lines.Add(string.Join("|",
                meta.Wing ?? "?",
                meta.Room ?? "?",
                meta.Date?.ToString("yyyy-MM-dd") ?? "?",
                stem));
        }

        // Content line
        var parts = new List<string>
        {
            $"0:{(entities.Length > 0 ? string.Join("+", entities) : "???")}",
            topics.Length > 0 ? string.Join("_", topics) : "misc",
        };
        if (!string.IsNullOrWhiteSpace(quote))
            parts.Add($"\"{EscapeQuote(quote)}\"");
        if (emotions.Length > 0)
            parts.Add(string.Join("+", emotions));
        if (flags.Length > 0)
            parts.Add(string.Join("+", flags));

        lines.Add(string.Join("|", parts));

        return string.Join('\n', lines);
    }

    IEnumerable<string> DetectEntities(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, code) in _codes)
            if (text.Contains(name, StringComparison.OrdinalIgnoreCase) && seen.Add(code))
                yield return code;

        foreach (Match m in EntityRegex().Matches(text))
        {
            if (StopWords.Contains(m.Value))
                continue;
            var code = Encode(m.Value);
            if (seen.Add(code))
                yield return code;
        }
    }

    string Encode(string name)
        => _codes.TryGetValue(name, out var c) ? c
           : new string(name.Where(char.IsLetterOrDigit)
                            .Take(3)
                            .Select(char.ToUpperInvariant)
                            .ToArray());

    static IEnumerable<string> ExtractTopics(string text)
        => TopicRegex().Matches(text.ToLowerInvariant())
                .Select(m => m.Value)
                .Where(w => !CommonWords.Contains(w))
                .GroupBy(w => w)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .Select(g => g.Key);

    static string ExtractKeySentence(string text)
    {
        var best = SentenceSplitRegex().Split(text.Trim())
            .Select(s => s.Trim())
            .Where(s => s.Length >= 20)
            .OrderByDescending(ScoreSentence)
            .FirstOrDefault();

        return best switch
        {
            null => "",
            { Length: <= 140 } => best,
            _ => $"{best[..137]}...",
        };
    }

    static int ScoreSentence(string s)
    {
        var l = s.ToLowerInvariant();
        var score = s.Length;
        foreach (var (weight, stems) in ScoreCues)
            if (stems.Any(l.Contains))
                score += weight;
        return score;
    }

    static IEnumerable<string> DetectEmotions(string text)
    {
        var lower = text.ToLowerInvariant();
        foreach (var (code, signals) in Emotions)
            if (signals.Any(lower.Contains))
                yield return code;
    }

    static IEnumerable<string> DetectFlags(string text)
    {
        var lower = text.ToLowerInvariant();
        foreach (var (flag, stems) in Flags)
            if (stems.Any(lower.Contains))
                yield return flag;
    }

    static string EscapeQuote(string v)
        => v.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
