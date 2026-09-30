namespace Jellyfin.Plugin.RatingsFixer.Configuration;

/// <summary>A country's rating ladders, loosest first.</summary>
/// <param name="Country">ISO 3166-1 alpha-2 code.</param>
/// <param name="Name">Display name.</param>
/// <param name="Movies">Ratings a movie can be given.</param>
/// <param name="Series">Ratings a series can be given.</param>
public sealed record LadderPreset(string Country, string Name, string Movies, string Series);

/// <summary>
/// Ready-made ladders for the countries whose ratings most users want. Every string must exist in
/// Jellyfin's rating table for that country, or the server can't score what the plugin writes — they
/// were checked against Jellyfin 12.1's tables. A preset only works when the libraries' metadata
/// country is that country.
/// </summary>
public static class LadderPresets
{
    /// <summary>Gets the presets.</summary>
    public static IReadOnlyList<LadderPreset> All { get; } =
    [
        new("US", "United States (MPA / TV Parental Guidelines)", "G, PG, PG-13, R, NC-17", "TV-Y, TV-Y7, TV-G, TV-PG, TV-14, TV-MA"),
        new("GB", "United Kingdom (BBFC)", "U, PG, 12A, 15, 18", "U, PG, 12, 15, 18"),
        new("CA", "Canada", "G, PG, 14A, 18A, R", "C, C8, G, PG, 14+, 18+"),
        new("IE", "Ireland (IFCO)", "G, 12A, 15A, 16, 18", "G, 12A, 15A, 16, 18"),
        new("DE", "Germany (FSK)", "0, 6, 12, 16, 18", "0, 6, 12, 16, 18"),
        new("AU", "Australia", "G, PG, M, MA15+, R18+", "G, PG, M, MA15+, R18+"),
        new("NZ", "New Zealand", "G, PG, M, R13, R15, R16, R18", "G, PG, M, R13, R15, R16, R18"),
    ];
}
