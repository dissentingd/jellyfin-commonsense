namespace Jellyfin.Plugin.CommonSense.Rating;

/// <summary>
/// Scores rating strings. In the plugin this is backed by Jellyfin's own
/// per-country rating tables, so the plugin and the server's enforcement always agree.
/// </summary>
public interface IRatingScale
{
    /// <summary>Scores a rating string.</summary>
    /// <param name="rating">The rating, e.g. <c>PG-13</c> or <c>15</c>.</param>
    /// <param name="country">ISO 3166-1 alpha-2 country whose table to use; null for the server's own.</param>
    /// <returns>The score, or null when the table doesn't know the rating.</returns>
    RatingScore? Score(string rating, string? country = null);
}
