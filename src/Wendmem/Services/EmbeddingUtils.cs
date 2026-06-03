using System.Globalization;

namespace Wendmem.Services;

/// <summary>
/// Shared utilities for embedding vector serialization.
/// Decoupled from any specific embedder implementation.
/// </summary>
static class EmbeddingUtils
{
    public static string ToFloatArrayLiteral(float[] vec)
    {
        return "[" + string.Join(", ",
            vec.Select(v => v.ToString("R", CultureInfo.InvariantCulture))
        ) + "]";
    }

    public static string ToDuckDbFloatArray(float[] vec)
        => $"{ToFloatArrayLiteral(vec)}::FLOAT[{vec.Length}]";
}
