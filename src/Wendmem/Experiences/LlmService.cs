using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Wendmem.Models;
using Wendmem.Serialization;

namespace Wendmem.Experiences;

/// <summary>
/// Wraps IChatClient with helpers for JSON-structured distillation responses.
/// All distillation goes through this - keeps prompt formatting + JSON
/// parsing in one place.
/// </summary>
public sealed class LlmService : IDisposable
{
    readonly IChatClient _chat;

    public string ModelName { get; }

    public LlmService(IChatClient chat, string modelName)
    {
        _chat = chat;
        ModelName = modelName;
    }

    public async Task<string> CompleteAsync(string prompt, CancellationToken ct)
    {
        var response = await _chat.GetResponseAsync(prompt, cancellationToken: ct);
        return response.Text ?? string.Empty;
    }

    /// <summary>
    /// Complete and parse a single ValidationVerdictDto from the LLM response.
    /// </summary>
    public async Task<ValidationVerdictDto?> CompleteValidationVerdictAsync(string prompt, CancellationToken ct)
    {
        var raw = await CompleteAsync(prompt, ct);
        var json = ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        { return JsonSerializer.Deserialize(json, WendmemJsonContext.Default.ValidationVerdictDto); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Complete and parse a single CallVerdictDto from the LLM response.
    /// </summary>
    public async Task<CallVerdictDto?> CompleteCallVerdictAsync(string prompt, CancellationToken ct)
    {
        var raw = await CompleteAsync(prompt, ct);
        var json = ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        { return JsonSerializer.Deserialize(json, WendmemJsonContext.Default.CallVerdictDto); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Complete and parse a list of RawMemoryDto from the LLM response.
    /// </summary>
    public async Task<List<RawMemoryDto>> CompleteRawMemoryListAsync(string prompt, CancellationToken ct)
    {
        var raw = await CompleteAsync(prompt, ct);
        var json = ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize(json, WendmemJsonContext.Default.ListRawMemoryDto) ?? [];
        }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// Strip ```json fences and any surrounding prose. Tolerant of LLM quirks.
    /// </summary>
    static string ExtractJson(string text)
    {
        var match = Regex.Match(text, @"```(?:json)?\s*(.*?)```", RegexOptions.Singleline);
        if (match.Success)
            return match.Groups[1].Value.Trim();

        var first = text.IndexOfAny(['{', '[']);
        if (first < 0)
            return text.Trim();

        var openChar = text[first];
        var closeChar = openChar == '{' ? '}' : ']';
        var depth = 0;
        for (var i = first; i < text.Length; i++)
        {
            if (text[i] == openChar)
                depth++;
            else if (text[i] == closeChar)
            {
                depth--;
                if (depth == 0)
                    return text[first..(i + 1)];
            }
        }
        return text[first..].Trim();
    }

    public void Dispose() => (_chat as IDisposable)?.Dispose();
}
