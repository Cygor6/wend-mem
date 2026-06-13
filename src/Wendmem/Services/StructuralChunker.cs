namespace Wendmem.Services;

/// <summary>
/// Structural text chunking. Replaces fixed-size chunking with
/// boundary detection at semantic shift points (headings, paragraph breaks,
/// class/function definitions, section dividers).
///
/// Two modes with separate budgets:
///  - Char mode: TargetSize/Min/MaxChunkSize.
///  - Token mode: TargetTokens/Min/MaxTokens, with char ceilings DERIVED from
///    the token constants (≈4 chars/token for sv/en text, with slack) so the
///    token budget is actually reachable. The old version reused the char-mode
///    MaxChunkSize (1200 chars ≈ 300 tokens), which made TargetTokens=512 and
///    MaxTokens=1024 unreachable.
/// </summary>
static class StructuralChunker
{
    // Char-based defaults (used when no embedder is available)
    const int TargetSize = 800;
    const int MinChunkSize = 200;
    const int MaxChunkSize = 1200;

    // NOTE: TargetSize + LookaheadWindow == MaxChunkSize with current values.
    // The lookahead break and the max break therefore coincide; if you change
    // any of the three, be aware they become independent limits.
    const int LookaheadWindow = 400;

    // Token-based defaults
    const int TargetTokens = 512;
    const int MinTokens = 64;
    const int MaxTokens = 1024;

    // Derived char ceilings for token mode. ~4 chars/token holds roughly for
    // both Swedish and English with multilingual BPE tokenizers; the *5 ceiling
    // adds slack and real token counts always have the final say.
    const int TokenModeTargetChars = TargetTokens * 4;   // 2048
    const int TokenModeMaxChars = MaxTokens * 5;         // 5120
    const int TokenModeMinChars = 50;                    // hard char floor

    /// <summary>
    /// Split text into chunks at Structural boundaries (char-based).
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
            if (chunks.Count > 0 && text.Length - start <= MaxChunkSize)
            {
                chunks.Add(text[start..].TrimEnd());
                break;
            }

            int cut = FindNextCut(text, start, boundaries, ref boundaryIdx);

            var chunk = text[start..cut].TrimEnd();
            if (chunk.Length > 0)
                chunks.Add(chunk);

            if (cut >= text.Length)
                break;

            start = cut;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
        }

        MergeUndersizedTrailingChunks(chunks, chunk => chunk.Length, MinChunkSize);

        return chunks;
    }

    /// <summary>
    /// Split text into chunks at Structural boundaries (token-aware).
    /// Uses the embedder's tokenizer to enforce the token budget; char limits
    /// derived from the token constants are only used as scan ceilings.
    /// </summary>
    internal static List<string> Chunk(string text, IEmbedder embedder)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        // ≈ TargetTokens or less for sv/en text — no tokenizer call needed.
        if (text.Length <= TargetTokens * 3)
            return [text];

        var boundaries = FindShiftBoundaries(text);

        if (boundaries.Count == 0)
            return FixedWindowChunkTokenAware(text, embedder);

        var chunks = new List<string>();
        int start = 0;
        int boundaryIdx = 0;

        while (start < text.Length)
        {
            int rest = text.Length - start;
            if (chunks.Count > 0 && rest <= TokenModeMaxChars
                && (rest <= TargetTokens * 3
                    || embedder.CountTokens(text[start..]) <= MaxTokens))
            {
                chunks.Add(text[start..].TrimEnd());
                break;
            }

            int cut = FindNextCutTokenAware(text, start, boundaries, ref boundaryIdx, embedder);

            var chunk = text[start..cut].TrimEnd();
            if (chunk.Length > 0)
                chunks.Add(chunk);

            if (cut >= text.Length)
                break;

            start = cut;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
        }

        MergeUndersizedTrailingChunks(chunks, embedder.CountTokens, MinTokens);

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
            else if (IsTypeOrNamespaceDeclaration(line))
                boundaries.Add(lineStart);
            else if (line.StartsWith("#region", StringComparison.OrdinalIgnoreCase))
                boundaries.Add(lineStart);
            else if (StartsWithKeyword(line, "def ", "fn ", "func ", "pub fn ", "pub struct ", "impl "))
                boundaries.Add(lineStart);
            else if (line.Length == 0 && i > 0 && i + 1 < lines.Length && lines[i + 1].Trim().Length > 0)
                boundaries.Add(lineStart); // paragraph break (single OR last-of-many blank lines)
            else if (IsSectionDivider(line))
                boundaries.Add(lineStart);

            pos += lines[i].Length + 1;
        }

        return boundaries;
    }

    // Leading modifiers stripped before checking for a type/namespace keyword,
    // so "public sealed partial class", "file record struct" etc. all count —
    // not just the exact combinations of a hardcoded list.
    static readonly string[] DeclarationModifiers =
    [
        "public ", "internal ", "private ", "protected ", "static ",
        "sealed ", "abstract ", "partial ", "file ", "readonly ", "ref ", "new ",
    ];

    static readonly string[] DeclarationKeywords =
    [
        "namespace ", "class ", "record ", "struct ", "interface ", "enum ",
    ];

    static bool IsTypeOrNamespaceDeclaration(string line)
    {
        var s = line.AsSpan();
        bool stripped = true;
        while (stripped)
        {
            stripped = false;
            foreach (var m in DeclarationModifiers)
            {
                if (s.StartsWith(m, StringComparison.Ordinal))
                {
                    s = s[m.Length..].TrimStart();
                    stripped = true;
                }
            }
        }

        foreach (var kw in DeclarationKeywords)
        {
            if (s.StartsWith(kw, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    static int FindNextCut(string text, int start, List<int> boundaries, ref int boundaryIdx)
    {
        if (text.Length - start <= MaxChunkSize)
            return text.Length;

        // Skip boundaries we've already passed (sentence-cut fallbacks land
        // between boundaries, so the index can lag behind start).
        while (boundaryIdx < boundaries.Count && boundaries[boundaryIdx] <= start)
            boundaryIdx++;

        int bestCut = -1;
        int bestDist = int.MaxValue;

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

        int sentenceCut = FindSentenceBoundary(text, start, Math.Min(start + TargetSize, text.Length));
        if (sentenceCut > start + MinChunkSize)
            return sentenceCut;

        return Math.Min(start + MaxChunkSize, text.Length);
    }

    static int FindNextCutTokenAware(string text, int start, List<int> boundaries,
        ref int boundaryIdx, IEmbedder embedder)
    {
        while (boundaryIdx < boundaries.Count && boundaries[boundaryIdx] <= start)
            boundaryIdx++;

        int bestCut = -1;
        int bestDeviation = int.MaxValue;

        // Incremental token counting: each inter-boundary segment is tokenized
        // once per cut search instead of re-tokenizing the whole growing prefix
        // for every candidate (which was O(n²) on boundary-dense code files).
        // BPE counts aren't perfectly additive across segment seams (±1 token
        // per join) — more than precise enough for sizing decisions.
        int segStart = start;
        int runningTokens = 0;

        for (int i = boundaryIdx; i < boundaries.Count; i++)
        {
            int b = boundaries[i];
            int dist = b - start;

            if (dist > TokenModeMaxChars)
                break;

            runningTokens += embedder.CountTokens(text[segStart..b]);
            segStart = b;

            if (dist < TokenModeMinChars)
                continue;
            if (runningTokens < MinTokens)
                continue;
            if (runningTokens > MaxTokens)
                break;

            int deviation = Math.Abs(runningTokens - TargetTokens);
            if (deviation < bestDeviation)
            {
                bestDeviation = deviation;
                bestCut = b;
            }

            if (runningTokens > TargetTokens + 256)
                break;
        }

        if (bestCut >= 0)
        {
            boundaryIdx = boundaries.BinarySearch(bestCut);
            if (boundaryIdx < 0)
                boundaryIdx = ~boundaryIdx;
            return bestCut;
        }

        // Fallback to sentence boundary near the token target.
        int charEstimate = Math.Min(start + TokenModeTargetChars, text.Length);
        int sentenceCut = FindSentenceBoundary(text, start, charEstimate);
        if (sentenceCut > start + TokenModeMinChars)
        {
            int tokens = embedder.CountTokens(text[start..sentenceCut]);
            if (tokens >= MinTokens && tokens <= MaxTokens)
                return sentenceCut;
        }

        // Last resort: cut at the target-equivalent char distance, which stays
        // within the token budget (unlike TokenModeMaxChars, which would not).
        return Math.Min(start + TokenModeTargetChars, text.Length);
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

    static List<string> FixedWindowChunkTokenAware(string text, IEmbedder embedder)
    {
        var chunks = new List<string>();
        int start = 0;

        while (start < text.Length)
        {
            if (text.Length - start <= TokenModeTargetChars)
            {
                chunks.Add(text[start..].TrimEnd());
                break;
            }

            int charEstimate = Math.Min(start + TokenModeTargetChars, text.Length);
            int cut = FindSentenceBoundary(text, start, charEstimate);

            int tokens = embedder.CountTokens(text[start..cut]);
            if (tokens > MaxTokens)
            {
                // Shrink: walk backward by sentence boundaries
                while (cut > start + MinChunkSize && tokens > MaxTokens)
                {
                    cut = FindSentenceBoundary(text, start, cut - 1);
                    if (cut <= start)
                    { cut = start + MinChunkSize; break; }
                    tokens = embedder.CountTokens(text[start..cut]);
                }
            }

            if (tokens < MinTokens && cut < text.Length)
            {
                // Extend: walk forward by sentence boundaries
                int limit = Math.Min(text.Length, start + TokenModeMaxChars);
                while (cut < limit && tokens < MinTokens)
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

        MergeUndersizedTrailingChunks(chunks, embedder.CountTokens, MinTokens);

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
