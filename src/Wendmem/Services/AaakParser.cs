using System.Text;
using Wendmem.Models;

namespace Wendmem.Services;

static class AaakParser
{
    public static AaakRecord Parse(string aaak)
    {
        var lines = aaak.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? wing = null, room = null, date = null, source = null;

        var contentLine = lines.FirstOrDefault(l =>
            l.StartsWith("0:", StringComparison.Ordinal));

        if (contentLine is not null && lines.Length > 1)
        {
            var header = lines[0].Split('|');
            if (header.Length >= 4)
            {
                wing = Normalize(header[0]);
                room = Normalize(header[1]);
                date = Normalize(header[2]);
                source = Normalize(header[3]);
            }
        }

        if (contentLine is null)
            throw new FormatException("AAAK content line starting with '0:' not found.");

        var parts = SplitPipeAware(contentLine);
        var entities = parts.Length > 0 ? parts[0]["0:".Length..] : "???";
        var topics = parts.Length > 1 ? parts[1] : "misc";
        var quote = parts.Length > 2 && parts[2].StartsWith('"')
                       ? Unquote(parts[2]) : null;
        var rest = parts.Skip(2).Where(p => !p.StartsWith('"')).ToArray();
        var emotions = rest.FirstOrDefault(IsEmotionList);
        var flags = rest.FirstOrDefault(IsFlagList);

        return new AaakRecord(wing, room, date, source,
                              entities, topics, quote, emotions, flags);
    }

    static string? Normalize(string s)
        => s == "?" || string.IsNullOrWhiteSpace(s) ? null : s;

    static string[] SplitPipeAware(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuote = false, escaped = false;

        foreach (var ch in line)
        {
            if (escaped)
            { current.Append(ch); escaped = false; continue; }
            if (ch == '\\')
            { current.Append(ch); escaped = true; continue; }
            if (ch == '"')
            { inQuote = !inQuote; current.Append(ch); continue; }
            if (ch == '|' && !inQuote)
            { result.Add(current.ToString()); current.Clear(); continue; }
            current.Append(ch);
        }
        result.Add(current.ToString());
        return [.. result];
    }

    static string Unquote(string v)
    {
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
            v = v[1..^1];
        return v.Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    static bool IsFlagList(string v)
        => v.Split('+').All(x => x.Length > 0 && x.All(c => char.IsUpper(c) || c == '_'));

    static bool IsEmotionList(string v)
        => v.Split('+').All(x => x.Length > 0 && x.All(c => char.IsLower(c)));
}
