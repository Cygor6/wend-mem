using System.Diagnostics;
using System.Numerics.Tensors;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Wendmem.Services;

namespace Wendmem.Chunkers;

/// <summary>
/// TA-Mem inspired topic-shift chunking. Splits text into sentences,
/// embeds them in a single batch, and cuts chunks at semantic boundaries
/// where cosine similarity between adjacent sentences drops below threshold.
/// Falls back to fixed-window when embedding is disabled or text is too short.
/// </summary>
sealed class TopicShiftChunker(IEmbedder embedder, PalaceConfig config, ILogger<TopicShiftChunker> logger)
{
    static readonly Regex SentenceSplitter = new(
        @"(?<=[.!?])\s+", RegexOptions.Compiled);

    public async Task<IReadOnlyList<string>> ChunkAsync(
        string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        int minTokens = Math.Max(16, config.MinChunkTokens);
        int targetTokens = config.TargetChunkTokens;
        int maxTokens = Math.Max(minTokens + 32, config.MaxChunkTokens);

        if (embedder.CountTokens(text) <= maxTokens)
            return [text];

        if (!config.TopicShiftChunkingEnabled)
            return ApplyOverlap(FixedWindowChunk(text, maxTokens, minTokens));

        var sentences = SplitSentences(text);

        if (sentences.Count < 3)
            return ApplyOverlap(FixedWindowChunk(text, maxTokens, minTokens));

        IReadOnlyList<float[]> vectors;
        var sw = Stopwatch.StartNew();
        try
        { vectors = await embedder.EmbedDocumentBatchAsync(sentences, ct); }
        catch { return ApplyOverlap(FixedWindowChunk(text, maxTokens, minTokens)); }
        sw.Stop();
        logger.LogDebug("EmbedDocumentBatchAsync: {Count} sentences in {Ms} ms",
            sentences.Count, sw.ElapsedMilliseconds);

        return ApplyOverlap(BuildChunks(sentences, vectors, minTokens, targetTokens, maxTokens));
    }

    List<string> BuildChunks(
        IReadOnlyList<string> sentences, IReadOnlyList<float[]> vectors,
        int minTokens, int targetTokens, int maxTokens)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        int currentTokens = 0;

        for (int i = 0; i < sentences.Count; i++)
        {
            int sentTokens = embedder.CountTokens(sentences[i]);

            if (sentTokens > maxTokens)
            {
                logger.LogWarning(
                    "chunker_sentence_exceeds_token_limit: {Preview} ({Tokens} > {Max})",
                    sentences[i][..Math.Min(80, sentences[i].Length)],
                    sentTokens, maxTokens);

                // Flush any accumulated content before the oversized sentence
                // so it becomes its own chunk rather than inflating a mixed chunk.
                if (current.Length > 0)
                {
                    chunks.Add(current.ToString().TrimEnd());
                    current.Clear();
                    currentTokens = 0;
                }
            }

            if (current.Length > 0 && i > 0)
            {
                float sim = CosineSimilarity(vectors[i - 1], vectors[i]);
                bool topicShift = sim < config.TopicShiftThreshold;
                bool hardTokenCap = currentTokens + sentTokens > maxTokens;

                // Soft break near target: if we've passed targetTokens, check if this
                // is a local similarity minimum within ±2 sentences.
                bool softBreak = false;
                if (currentTokens >= targetTokens && !topicShift && !hardTokenCap)
                {
                    softBreak = IsLocalMinimum(vectors, i, window: 2)
                        && currentTokens + sentTokens > targetTokens;
                }

                if ((currentTokens >= minTokens && topicShift) || hardTokenCap || softBreak)
                {
                    chunks.Add(current.ToString().TrimEnd());
                    current.Clear();
                    currentTokens = 0;
                }
            }

            if (current.Length > 0)
                current.Append(' ');
            current.Append(sentences[i]);
            currentTokens += sentTokens;
        }

        if (current.Length > 0)
            chunks.Add(current.ToString().TrimEnd());

        return MergeUndersized(chunks, minTokens);
    }

    static List<string> SplitSentences(string text)
    {
        var blocks = text.Split(["\n\n"], StringSplitOptions.None);
        var sentences = new List<string>();

        foreach (var block in blocks)
        {
            var trimmed = block.Trim();
            if (trimmed.Length == 0)
                continue;

            var sents = SentenceSplitter.Split(trimmed)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList();

            if (sents.Count > 0)
                sentences.AddRange(sents);
            else if (trimmed.Length > 0)
                sentences.Add(trimmed);
        }

        return sentences;
    }

    /// <summary>
    /// When ChunkOverlapSentences > 0, prepend the last N sentences of the
    /// previous chunk to each chunk (except the first).
    /// </summary>
    IReadOnlyList<string> ApplyOverlap(IReadOnlyList<string> chunks)
    {
        int overlap = config.ChunkOverlapSentences;
        if (overlap <= 0 || chunks.Count < 2)
            return chunks;

        var result = new List<string>(chunks.Count);
        result.Add(chunks[0]);

        for (int i = 1; i < chunks.Count; i++)
        {
            var prevSentences = SplitSentences(chunks[i - 1]);
            int take = Math.Min(overlap, prevSentences.Count);
            if (take > 0)
            {
                // Collect overlap sentences from the tail of the previous chunk,
                // respecting the token cap.
                var tail = new List<string>();
                int overlapTokens = 0;
                int chunkTokens = embedder.CountTokens(chunks[i]);
                int cap = config.MaxChunkTokens;

                for (int s = prevSentences.Count - 1; s >= 0 && tail.Count < take; s--)
                {
                    int t = embedder.CountTokens(prevSentences[s]);
                    if (overlapTokens + t + chunkTokens > cap)
                        break;
                    overlapTokens += t;
                    tail.Add(prevSentences[s]);
                }

                if (tail.Count > 0)
                {
                    tail.Reverse();
                    result.Add(string.Join(" ", tail) + "\n" + chunks[i]);
                }
                else
                {
                    result.Add(chunks[i]);
                }
            }
            else
            {
                result.Add(chunks[i]);
            }
        }

        return result;
    }

    List<string> FixedWindowChunk(string text, int maxTokens, int minTokens)
    {
        var chunks = new List<string>();
        int start = 0;

        while (start < text.Length)
        {
            int cut = FindNextSentenceBoundary(text, start);

            while (cut < text.Length)
            {
                int nextCut = FindNextSentenceBoundary(text, cut);
                if (nextCut <= cut)
                    break;
                if (embedder.CountTokens(text[start..nextCut]) > maxTokens)
                    break;
                cut = nextCut;
            }

            if (embedder.CountTokens(text[start..cut]) > maxTokens)
                cut = ShrinkToTokenBudget(text, start, cut, maxTokens);

            if (embedder.CountTokens(text[start..cut]) < minTokens && cut < text.Length)
                cut = ExtendToTokenFloor(text, start, cut, minTokens);

            chunks.Add(text[start..cut].TrimEnd());
            start = cut;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
        }

        return MergeUndersized(chunks, minTokens);
    }

    int ShrinkToTokenBudget(string text, int start, int cut, int maxTokens)
    {
        int search = cut;
        while (search > start)
        {
            search = FindPrevSentenceBoundary(text, search);
            if (search <= start)
                break;
            if (embedder.CountTokens(text[start..search]) <= maxTokens)
                return search;
        }
        // Pathological: single sentence exceeds maxTokens.
        // Let it through as an oversized chunk — storing beats dropping.
        // BuildChunks logs the warning; here we log for the fixed-window path.
        if (logger.IsEnabled(LogLevel.Warning))
            logger.LogWarning(
                "chunker_sentence_exceeds_token_limit: {Preview} ({Tokens} > {Max})",
                text[start..Math.Min(start + 80, text.Length)],
                embedder.CountTokens(text[start..cut]), maxTokens);
        return cut;
    }

    int ExtendToTokenFloor(string text, int start, int cut, int minTokens)
    {
        while (cut < text.Length)
        {
            int nextCut = FindNextSentenceBoundary(text, cut);
            if (nextCut <= cut)
                break;
            if (embedder.CountTokens(text[start..nextCut]) >= minTokens)
                return nextCut;
            cut = nextCut;
        }
        return cut;
    }

    static int FindNextSentenceBoundary(string text, int from)
    {
        for (int i = from; i < text.Length; i++)
        {
            if (text[i] is '.' or '!' or '?')
            {
                if (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]))
                    return i + 1;
            }
            if (text[i] == '\n')
                return i + 1;
        }
        return text.Length;
    }

    List<string> MergeUndersized(List<string> chunks, int minTokens)
    {
        while (chunks.Count >= 2 && embedder.CountTokens(chunks[^1]) < minTokens)
        {
            var last = chunks[^1];
            chunks.RemoveAt(chunks.Count - 1);
            chunks[^1] = chunks[^1] + "\n" + last;
        }
        return chunks;
    }

    /// <summary>
    /// Walk backward from 'from' to find the previous sentence boundary.
    /// </summary>
    static int FindPrevSentenceBoundary(string text, int from)
    {
        for (int i = from - 1; i >= 0; i--)
        {
            if (text[i] is '.' or '!' or '?' &&
                (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])))
                return i + 1;
            if (text[i] == '\n')
                return i + 1;
        }
        return 0;
    }

    /// <summary>
    /// Checks if the similarity between sentence idx-1 and idx is a local minimum
    /// within ±window sentences. Returns true only if no neighbour boundary has
    /// lower similarity — i.e. this is the "least similar" transition nearby.
    /// </summary>
    static bool IsLocalMinimum(IReadOnlyList<float[]> vectors, int idx, int window)
    {
        if (idx <= 0 || idx >= vectors.Count)
            return false;

        float center = CosineSimilarity(vectors[idx - 1], vectors[idx]);

        for (int d = 1; d <= window; d++)
        {
            int left = idx - d;
            if (left > 0 && CosineSimilarity(vectors[left - 1], vectors[left]) < center)
                return false;

            int right = idx + d;
            if (right < vectors.Count && CosineSimilarity(vectors[right - 1], vectors[right]) < center)
                return false;
        }

        return true;
    }

    static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = TensorPrimitives.Dot(a, b);
        float normA = TensorPrimitives.Norm(a);
        float normB = TensorPrimitives.Norm(b);
        float denom = normA * normB;
        return denom < 1e-9f ? 0f : dot / denom;
    }
}
