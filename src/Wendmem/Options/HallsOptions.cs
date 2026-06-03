namespace Wendmem.Options;

/// <summary>
/// Bound from appsettings.json "Halls" section.
/// Each child key is a hall (room) name; values are keyword arrays.
/// Bound via GetSection().Get&lt;Dictionary&gt;() which the source generator intercepts.
/// </summary>
public static class HallsOptions
{
    public const string SectionName = "Halls";
}
