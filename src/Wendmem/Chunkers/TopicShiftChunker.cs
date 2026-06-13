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
///
/// Performance notes:
///  - Adjacent similarities are computed ONCE into an array (norms included);
///    the old version recomputed the same cosines inside IsLocalMinimum for
///    every soft-break probe.
///  - Fixed-window chunking accumulates token counts per sentence segment
///    instead of re-tokenizing the growing prefix per extension step (O(n²)
///    tokenizer calls on long texts). BPE counts aren't perfectly additive
///    across seams (±1 token) — irrelevant for sizing decisions.
/// </summary>
sealed partial class TopicShiftChunker(IEmbedder embedder, PalaceConfig config, ILogger<TopicShiftChunker> logger)
{
    // GeneratedRegex instead of RegexOptions.Compiled: under NativeAOT the
    // Compiled flag is silently ignored and falls back to the interpreter.
    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceSplitter();

    public async Task<IReadOnlyList<string>> ChunkAsync(
        string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        int minTokens = Math.Max(16, config.MinChunkTokens);
        int maxTokens = Math.Max(minTokens + 32, config.MaxChunkTokens);
        int targetTokens = Math.Clamp(config.TargetChunkTokens, minTokens, maxTokens);

        if (embedder.CountTokens(text) <= maxTokens)
            return [text];

        if (!config.TopicShiftChunkingEnabled)
            return ApplyOverlap(FixedWindowChunk(text, maxTokens, minTokens), maxTokens);

        var sentences = SplitSentences(text);

        if (sentences.Count < 3)
            return ApplyOverlap(FixedWindowChunk(text, maxTokens, minTokens), maxTokens);

        IReadOnlyList<float[]> vectors;
        var sw = Stopwatch.StartNew();
        try
        {
            vectors = await embedder.EmbedDocumentBatchAsync(sentences, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // never swallow cancellation
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sentence batch embedding failed; falling back to fixed-window chunking");
            return ApplyOverlap(FixedWindowChunk(text, maxTokens, minTokens), maxTokens);
        }
        sw.Stop();
        logger.LogDebug("EmbedDocumentBatchAsync: {Count} sentences in {Ms} ms",
            sentences.Count, sw.ElapsedMilliseconds);

        if (vectors.Count != sentences.Count)
        {
            logger.LogWarning("Embedder returned {Got} vectors for {Expected} sentences; falling back",
                vectors.Count, sentences.Count);
            return ApplyOverlap(FixedWindowChunk(text, maxTokens, minTokens), maxTokens);
        }

        var sims = AdjacentSimilarities(vectors);
        return ApplyOverlap(BuildChunks(sentences, sims, minTokens, targetTokens, maxTokens), maxTokens);
    }

    /// <summary>
    /// sims[i] = cosine(vectors[i-1], vectors[i]) for i in 1..n-1; sims[0] unused.
    /// Norms are computed once per vector instead of twice per pair.
    /// </summary>
    static float[] AdjacentSimilarities(IReadOnlyList<float[]> vectors)
    {
        var norms = new float[vectors.Count];
        for (int i = 0; i < vectors.Count; i++)
            norms[i] = TensorPrimitives.Norm(vectors[i]);

        var sims = new float[vectors.Count];
        for (int i = 1; i < vectors.Count; i++)
        {
            float denom = norms[i - 1] * norms[i];
            sims[i] = denom < 1e-9f
                ? 0f
                : TensorPrimitives.Dot(vectors[i - 1], vectors[i]) / denom;
        }
        return sims;
    }

    List<string> BuildChunks(
        IReadOnlyList<string> sentences, float[] sims,
        int minTokens, int targetTokens, int maxTokens)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        int currentTokens = 0;

        void Flush()
        {
            if (current.Length > 0)
            {
                chunks.Add(current.ToString().TrimEnd());
                current.Clear();
                currentTokens = 0;
            }
        }

        for (int i = 0; i < sentences.Count; i++)
        {
            int sentTokens = embedder.CountTokens(sentences[i]);

            if (sentTokens > maxTokens)
            {
                // Typically a code block or similar without sentence punctuation
                // (SplitSentences only splits on \n\n + [.!?]). Don't store it as
                // one oversized chunk — the embedder would truncate it and the
                // tail would be semantically unsearchable. Hard-split it instead;
                // FixedWindowChunk cuts at newlines/sentence ends and, as a last
                // resort, at whitespace near the token budget.
                logger.LogWarning(
                    "chunker_sentence_exceeds_token_limit: {Preview} ({Tokens} > {Max})",
                    sentences[i][..Math.Min(80, sentences[i].Length)],
                    sentTokens, maxTokens);

                Flush();
                chunks.AddRange(FixedWindowChunk(sentences[i], maxTokens, minTokens));
                continue;
            }

            if (current.Length > 0 && i > 0)
            {
                bool topicShift = sims[i] < config.TopicShiftThreshold;
                bool hardTokenCap = currentTokens + sentTokens > maxTokens;

                // Soft break: past the target and at a local similarity minimum
                // within ±2 sentences — the "least similar" transition nearby.
                bool softBreak = !topicShift && !hardTokenCap
                    && currentTokens >= targetTokens
                    && IsLocalMinimum(sims, i, window: 2);

                if ((currentTokens >= minTokens && topicShift) || hardTokenCap || softBreak)
                    Flush();
            }

            if (current.Length > 0)
                current.Append(' ');
            current.Append(sentences[i]);
            currentTokens += sentTokens;
        }

        Flush();

        return MergeUndersized(chunks, minTokens, maxTokens);
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

            var sents = SentenceSplitter().Split(trimmed)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList();

            if (sents.Count > 0)
                sentences.AddRange(sents);
            else
                sentences.Add(trimmed);
        }

        return sentences;
    }

    /// <summary>
    /// When ChunkOverlapSentences > 0, prepend the last N sentences of the
    /// previous chunk to each chunk (except the first), respecting the token cap.
    /// </summary>
    IReadOnlyList<string> ApplyOverlap(IReadOnlyList<string> chunks, int maxTokens)
    {
        int overlap = config.ChunkOverlapSentences;
        if (overlap <= 0 || chunks.Count < 2)
            return chunks;

        var result = new List<string>(chunks.Count)
        {
            chunks[0],
        };

        for (int i = 1; i < chunks.Count; i++)
        {
            var prevSentences = SplitSentences(chunks[i - 1]);
            int take = Math.Min(overlap, prevSentences.Count);

            if (take > 0)
            {
                var tail = new List<string>();
                int overlapTokens = 0;
                int chunkTokens = embedder.CountTokens(chunks[i]);

                for (int s = prevSentences.Count - 1; s >= 0 && tail.Count < take; s--)
                {
                    int t = embedder.CountTokens(prevSentences[s]);
                    if (overlapTokens + t + chunkTokens > maxTokens)
                        break;
                    overlapTokens += t;
                    tail.Add(prevSentences[s]);
                }

                if (tail.Count > 0)
                {
                    tail.Reverse();
                    result.Add(string.Join(" ", tail) + "\n" + chunks[i]);
                    continue;
                }
            }

            result.Add(chunks[i]);
        }

        return result;
    }

    /// <summary>
    /// Sentence/newline-bounded fixed-window chunking with incremental token
    /// accumulation. Each segment is tokenized exactly once. A single segment
    /// over the budget (no punctuation, no newlines) is hard-cut at whitespace
    /// near the char-equivalent of the token budget.
    /// </summary>
    List<string> FixedWindowChunk(string text, int maxTokens, int minTokens)
    {
        var chunks = new List<string>();
        int start = 0;

        while (start < text.Length)
        {
            int cut = start;
            int tokens = 0;

            while (cut < text.Length)
            {
                int next = FindNextSentenceBoundary(text, cut);
                if (next <= cut)
                    next = text.Length;

                int segTokens = embedder.CountTokens(text[cut..next]);

                if (tokens == 0 && segTokens > maxTokens)
                {
                    // ~3 chars/token keeps the hard cut under budget for sv/en.
                    int hardEnd = Math.Min(cut + maxTokens * 3, text.Length);
                    int ws = LastWhitespaceBefore(text, hardEnd, cut);
                    cut = ws > cut ? ws : hardEnd;
                    tokens = embedder.CountTokens(text[start..cut]);
                    break;
                }

                if (tokens > 0 && tokens + segTokens > maxTokens)
                    break;

                tokens += segTokens;
                cut = next;
            }

            if (cut <= start)
                break; // safety: cannot make progress

            if (tokens > maxTokens)
                logger.LogWarning(
                    "chunker_sentence_exceeds_token_limit: {Preview} ({Tokens} > {Max})",
                    text[start..Math.Min(start + 80, text.Length)], tokens, maxTokens);

            chunks.Add(text[start..cut].TrimEnd());
            start = cut;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
        }

        return MergeUndersized(chunks, minTokens, maxTokens);
    }

    /// <summary>
    /// Last whitespace in the window (end-200, end), exclusive of the floor.
    /// Returns the position after the whitespace, or -1 if none found.
    /// </summary>
    static int LastWhitespaceBefore(string text, int end, int floor)
    {
        int stop = Math.Max(floor + 1, end - 200);
        for (int i = Math.Min(end, text.Length) - 1; i >= stop; i--)
        {
            if (char.IsWhiteSpace(text[i]))
                return i + 1;
        }
        return -1;
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

    /// <summary>
    /// Merge undersized trailing chunks into their predecessor — but never past
    /// the token budget. A small tail is better than a chunk the embedder truncates.
    /// </summary>
    List<string> MergeUndersized(List<string> chunks, int minTokens, int maxTokens)
    {
        while (chunks.Count >= 2)
        {
            int lastTokens = embedder.CountTokens(chunks[^1]);
            if (lastTokens >= minTokens)
                break;
            if (embedder.CountTokens(chunks[^2]) + lastTokens > maxTokens)
                break;

            var last = chunks[^1];
            chunks.RemoveAt(chunks.Count - 1);
            chunks[^1] = chunks[^1] + "\n" + last;
        }
        return chunks;
    }

    /// <summary>
    /// Checks if sims[idx] is a local minimum within ±window boundaries.
    /// Returns true only if no neighbour boundary has lower similarity —
    /// i.e. this is the "least similar" transition nearby.
    /// </summary>
    static bool IsLocalMinimum(float[] sims, int idx, int window)
    {
        if (idx <= 0 || idx >= sims.Length)
            return false;

        float center = sims[idx];

        for (int d = 1; d <= window; d++)
        {
            int left = idx - d;
            if (left >= 1 && sims[left] < center)
                return false;

            int right = idx + d;
            if (right < sims.Length && sims[right] < center)
                return false;
        }

        return true;
    }
}
