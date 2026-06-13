using System.Text;

namespace Wendmem.Services;

/// <summary>
/// Shared text normalization helpers for canonical keys in the knowledge graph.
///
/// Why not string.Normalize(FormD)? Under NativeAOT with InvariantGlobalization
/// enabled, Unicode normalization throws PlatformNotSupportedException for
/// non-ASCII input. An explicit fold table is deterministic, allocation-light
/// and covers the Nordic/European characters that actually occur in
/// Swedish/English mixed content.
/// </summary>
static class TextNormalization
{
    /// <summary>
    /// Folds common Nordic/European letters to ASCII equivalents
    /// (å→a, ä→a, ö→o, é→e, ø→o, æ→ae, ß→ss, ...).
    /// Returns the original string instance if it is already pure ASCII.
    /// Characters without a mapping are passed through unchanged
    /// (callers strip remaining non-ASCII separately if needed).
    /// </summary>
    public static string FoldToAscii(string input)
    {
        StringBuilder? sb = null;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];

            if (c < 128)
            {
                sb?.Append(c);
                continue;
            }

            // First non-ASCII char: copy the ASCII prefix we skipped.
            sb ??= new StringBuilder(input.Length + 4).Append(input, 0, i);

            sb.Append(c switch
            {
                'å' or 'ä' or 'à' or 'á' or 'â' or 'ã' => "a",
                'Å' or 'Ä' or 'À' or 'Á' or 'Â' or 'Ã' => "A",
                'ö' or 'ò' or 'ó' or 'ô' or 'õ' => "o",
                'Ö' or 'Ò' or 'Ó' or 'Ô' or 'Õ' => "O",
                'é' or 'è' or 'ê' or 'ë' => "e",
                'É' or 'È' or 'Ê' or 'Ë' => "E",
                'í' or 'ì' or 'î' or 'ï' => "i",
                'Í' or 'Ì' or 'Î' or 'Ï' => "I",
                'ú' or 'ù' or 'û' or 'ü' => "u",
                'Ú' or 'Ù' or 'Û' or 'Ü' => "U",
                'ý' or 'ÿ' => "y",
                'Ý' => "Y",
                'ø' => "o",
                'Ø' => "O",
                'æ' => "ae",
                'Æ' => "AE",
                'œ' => "oe",
                'Œ' => "OE",
                'ß' => "ss",
                'ç' => "c",
                'Ç' => "C",
                'ñ' => "n",
                'Ñ' => "N",
                'š' => "s",
                'Š' => "S",
                'ž' => "z",
                'Ž' => "Z",
                'ð' => "d",
                'Ð' => "D",
                'þ' => "th",
                'Þ' => "Th",
                'ł' => "l",
                'Ł' => "L",
                _ => c.ToString()
            });
        }

        return sb?.ToString() ?? input;
    }
}
