using Wendmem.Wiki;

namespace Wendmem.Cli;

internal static class ArgvHelpers
{
    public static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
                return args[i + 1];
        return null;
    }

    public static string GetWing(string[] args, PalaceConfig config)
    {
        var wing = GetOption(args, "--wing");
        return PathValidator.ResolveWing(wing, config);
    }

    public static int GetIntOption(string[] args, string name, int fallback)
    {
        var raw = GetOption(args, name);
        return raw is not null && int.TryParse(raw, out var n) ? n : fallback;
    }

    public static float GetFloatOption(string[] args, string name, float fallback)
    {
        var raw = GetOption(args, name);
        return raw is not null && float.TryParse(raw, out var f) ? f : fallback;
    }

    public static bool HasFlag(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
            if (args[i] == name)
                return true;
        return false;
    }

    public static string? GetPositional(string[] args, int index)
    {
        int seen = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            { i++; continue; } // skip option + value
            if (seen == index)
                return args[i];
            seen++;
        }
        return null;
    }
}
