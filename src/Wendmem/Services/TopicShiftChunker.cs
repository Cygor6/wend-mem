namespace Wendmem.Services;

/// <summary>
/// Topic-shift chunking. Replaces fixed-size chunking with
/// boundary detection at semantic shift points (headings, blank lines,
/// class/function definitions, topic-word changes).
/// </summary>
static class TopicShiftChunker
{
    // Char-based defaults (used when no embedder is available)
    const int TargetSize = 800;
    const int MinChunkSize = 200;
    const int MaxChunkSize = 1200;
    const int LookaheadWindow = 400;

    // Token-based defaults
    const int TargetTokens = 512;
    const int MinTokens = 64;
    const int MaxTokens = 1024;

    /// <summary>
    /// Split text into chunks at topic-shift boundaries (char-based).
    /// Falls back to fixed-window chunking when no shift signals are found.
    /// </summary>
    internal static List<string> Chunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        if (text.Length <= TargetSize)
            return [text];

        var boundaries = FindShiftBoundaries(text);

        if (boundaries.Count == 0)
            return FixedWindowChunk(text);

        var chunks = new List<string>();
        int start = 0;
        int boundaryIdx = 0;

        while (start < text.Length)
        {
            int cut = FindNextCut(text, start, boundaries, ref boundaryIdx);

            if (text.Length - start <= MaxChunkSize && chunks.Count > 0)
            {
                chunks.Add(text[start..].TrimEnd());
                break;
            }

            var chunk = text[start..cut].TrimEnd();
            if (chunk.Length > 0)
                chunks.Add(chunk);

            start = cut;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
        }

        MergeUndersizedTrailingChunks(chunks, chunk => chunk.Length, MinChunkSize);

        return chunks;
    }

    /// <summary>
    /// Split text into chunks at topic-shift boundaries (token-aware).
    /// Uses the embedder's tokenizer to enforce token-based size limits.
    /// Falls back to char-based heuristic chunking when no embedder is provided.
    /// </summary>
    internal static List<string> Chunk(string text, IEmbedder embedder)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        int minT = Math.Max(16, MinTokens);
        int maxT = Math.Max(minT + 32, MaxTokens);
        int minC = Math.Max(50, MinChunkSize);
        int maxC = Math.Max(minC + 100, MaxChunkSize);

        if (text.Length <= minC)
            return [text];

        var boundaries = FindShiftBoundaries(text);

        if (boundaries.Count == 0)
            return FixedWindowChunkTokenAware(text, embedder, maxT, minT, maxC, minC);

        var chunks = new List<string>();
        int start = 0;
        int boundaryIdx = 0;

        while (start < text.Length)
        {
            int cut = FindNextCutTokenAware(text, start, boundaries, ref boundaryIdx, embedder, maxT, minT, maxC);

            if (text.Length - start <= maxC && chunks.Count > 0)
            {
                chunks.Add(text[start..].TrimEnd());
                break;
            }

            var chunk = text[start..cut].TrimEnd();
            if (chunk.Length > 0)
                chunks.Add(chunk);

            start = cut;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
        }

        MergeUndersizedTrailingChunks(chunks, embedder.CountTokens, minT);

        return chunks;
    }

    /// <summary>
    /// Find positions in the text where the topic likely shifts.
    /// </summary>
    static List<int> FindShiftBoundaries(string text)
    {
        var boundaries = new List<int>();
        var lines = text.Split('\n');

        int pos = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            var lineStart = pos + (lines[i].Length - line.Length);

            if (line.StartsWith('#') && line.Length > 1 && line[1] is ' ' or '#')
                boundaries.Add(lineStart);
            else if (StartsWithKeyword(line, "namespace ", "public class ", "internal class ",
                "public sealed class ", "public record ", "internal sealed class ",
                "public struct ", "public interface ", "public enum ",
                "internal record ", "private class ", "static class "))
                boundaries.Add(lineStart);
            else if (line.StartsWith("#region", StringComparison.OrdinalIgnoreCase))
                boundaries.Add(lineStart);
            else if (StartsWithKeyword(line, "def ", "class ", "fn ", "func ", "pub fn ", "pub struct ", "impl "))
                boundaries.Add(lineStart);
            else if (line.Length == 0 && i > 0 && lines[i - 1].Trim().Length == 0 && i + 1 < lines.Length && lines[i + 1].Trim().Length > 0)
                boundaries.Add(lineStart);
            else if (IsSectionDivider(line))
                boundaries.Add(lineStart);

            pos += lines[i].Length + 1;
        }

        return boundaries;
    }

    static int FindNextCut(string text, int start, List<int> boundaries, ref int boundaryIdx)
    {
        int endEstimate = Math.Min(start + TargetSize, text.Length);

        if (text.Length - start <= MaxChunkSize)
            return text.Length;

        int bestCut = -1;
        int bestDist = int.MaxValue;
        int sentenceCut = FindSentenceBoundary(text, start, endEstimate);

        for (int i = boundaryIdx; i < boundaries.Count; i++)
        {
            int b = boundaries[i];
            int dist = b - start;

            if (dist < MinChunkSize)
                continue;
            if (dist > MaxChunkSize)
                break;

            int deviation = Math.Abs(dist - TargetSize);
            if (deviation < bestDist)
            {
                bestDist = deviation;
                bestCut = b;
            }

            if (dist > TargetSize + LookaheadWindow)
                break;
        }

        if (bestCut >= 0)
        {
            boundaryIdx = boundaries.BinarySearch(bestCut);
            if (boundaryIdx < 0)
                boundaryIdx = ~boundaryIdx;
            return bestCut;
        }

        if (sentenceCut > start + MinChunkSize)
            return sentenceCut;

        return Math.Min(start + MaxChunkSize, text.Length);
    }

    static int FindNextCutTokenAware(string text, int start, List<int> boundaries,
        ref int boundaryIdx, IEmbedder embedder, int maxTokens, int minTokens, int maxChars)
    {
        if (text.Length - start <= maxChars)
            return text.Length;

        int bestCut = -1;
        int bestDeviation = int.MaxValue;

        for (int i = boundaryIdx; i < boundaries.Count; i++)
        {
            int b = boundaries[i];
            int dist = b - start;

            if (dist < 50)
                continue; // hard char floor
            if (dist > maxChars)
                break;

            int tokens = embedder.CountTokens(text[start..b]);
            if (tokens < minTokens)
                continue;
            if (tokens > maxTokens)
                break;

            int deviation = Math.Abs(tokens - TargetTokens);
            if (deviation < bestDeviation)
            {
                bestDeviation = deviation;
                bestCut = b;
            }

            if (tokens > TargetTokens + 256)
                break;
        }

        if (bestCut >= 0)
        {
            boundaryIdx = boundaries.BinarySearch(bestCut);
            if (boundaryIdx < 0)
                boundaryIdx = ~boundaryIdx;
            return bestCut;
        }

        // Fallback to sentence boundary, respecting token cap
        int charEstimate = Math.Min(start + maxTokens * 4, start + maxChars);
        int sentenceCut = FindSentenceBoundary(text, start, Math.Min(charEstimate, text.Length));
        if (sentenceCut > start + 50)
        {
            int tokens = embedder.CountTokens(text[start..sentenceCut]);
            if (tokens >= minTokens && tokens <= maxTokens)
                return sentenceCut;
        }

        return Math.Min(start + maxChars, text.Length);
    }

    static int FindSentenceBoundary(string text, int start, int end)
    {
        int searchStart = Math.Max(start + 50, end - 200);
        int best = -1;

        for (int i = end; i >= searchStart; i--)
        {
            if (i >= text.Length)
                continue;
            if (text[i] is '.' or '!' or '?')
            {
                if (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]))
                {
                    best = i + 1;
                    break;
                }
            }
        }

        if (best > 0)
            return best;

        for (int i = Math.Min(end, text.Length - 1); i >= searchStart; i--)
        {
            if (text[i] == '\n')
                return i + 1;
        }

        return Math.Min(end, text.Length);
    }

    static bool StartsWithKeyword(string line, params ReadOnlySpan<string> keywords)
    {
        foreach (var kw in keywords)
        {
            if (line.StartsWith(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static bool IsSectionDivider(string line)
    {
        if (line.Length < 3)
            return false;
        var trimmed = line.Trim();
        if (trimmed.Length < 3)
            return false;

        char c = trimmed[0];
        if (c is not ('-' or '=' or '*' or '/'))
            return false;

        int same = 0;
        foreach (char ch in trimmed)
        {
            if (ch == c)
                same++;
            else if (ch == ' ')
                continue;
            else
                return false;
        }
        return same >= 3;
    }

    static List<string> FixedWindowChunk(string text)
    {
        var chunks = new List<string>();
        int start = 0;

        while (start < text.Length)
        {
            if (text.Length - start <= TargetSize)
            {
                chunks.Add(text[start..].TrimEnd());
                break;
            }

            int cut = FindSentenceBoundary(text, start, start + TargetSize);
            if (cut <= start + MinChunkSize)
                cut = Math.Min(start + TargetSize, text.Length);

            chunks.Add(text[start..cut].TrimEnd());
            start = cut;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
        }

        MergeUndersizedTrailingChunks(chunks, chunk => chunk.Length, MinChunkSize);

        return chunks;
    }

    static List<string> FixedWindowChunkTokenAware(string text, IEmbedder embedder,
        int maxTokens, int minTokens, int maxChars, int minChars)
    {
        var chunks = new List<string>();
        int start = 0;

        while (start < text.Length)
        {
            if (text.Length - start <= maxChars)
            {
                chunks.Add(text[start..].TrimEnd());
                break;
            }

            int charEstimate = Math.Min(start + maxTokens * 4, start + maxChars);
            int cut = FindSentenceBoundary(text, start, Math.Min(charEstimate, text.Length));

            int tokens = embedder.CountTokens(text[start..cut]);
            if (tokens > maxTokens)
            {
                // Shrink: walk backward by sentence boundaries
                while (cut > start + minChars && tokens > maxTokens)
                {
                    cut = FindSentenceBoundary(text, start, cut - 1);
                    if (cut <= start)
                    { cut = start + minChars; break; }
                    tokens = embedder.CountTokens(text[start..cut]);
                }
            }

            if (tokens < minTokens && cut < text.Length)
            {
                // Extend: walk forward by sentence boundaries
                int limit = Math.Min(text.Length, start + maxChars);
                while (cut < limit && tokens < minTokens)
                {
                    int next = cut;
                    for (int i = cut; i < limit; i++)
                    {
                        if (text[i] is '.' or '!' or '?' &&
                            (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])))
                        { next = i + 1; break; }
                        if (text[i] == '\n')
                        { next = i + 1; break; }
                    }
                    if (next <= cut)
                        break;
                    cut = next;
                    tokens = embedder.CountTokens(text[start..cut]);
                }
            }

            chunks.Add(text[start..cut].TrimEnd());
            start = cut;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
        }

        MergeUndersizedTrailingChunks(chunks, embedder.CountTokens, minTokens);

        return chunks;
    }


    static void MergeUndersizedTrailingChunks(
        List<string> chunks,
        Func<string, int> measure,
        int minimumSize)
    {
        while (chunks.Count >= 2 && measure(chunks[^1]) < minimumSize)
        {
            var last = chunks[^1];
            chunks.RemoveAt(chunks.Count - 1);
            chunks[^1] = chunks[^1] + "\n" + last;
        }
    }
}
