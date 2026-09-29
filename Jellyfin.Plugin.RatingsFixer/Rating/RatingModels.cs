namespace Jellyfin.Plugin.RatingsFixer.Rating;

/// <summary>
/// A position on Jellyfin's common parental-rating scale. Scores are roughly
/// "minimum age"; the sub-score splits equal scores (US <c>R</c> is 17,
/// <c>NC-17</c> is 17 with sub-score 1).
/// </summary>
/// <param name="Score">The score.</param>
/// <param name="SubScore">The optional sub-score.</param>
public readonly record struct RatingScore(int Score, int? SubScore = null) : IComparable<RatingScore>
{
    /// <inheritdoc />
    public int CompareTo(RatingScore other)
    {
        var byScore = Score.CompareTo(other.Score);
        return byScore != 0 ? byScore : (SubScore ?? 0).CompareTo(other.SubScore ?? 0);
    }

    /// <summary>Greater-than.</summary>
    /// <param name="a">Left.</param>
    /// <param name="b">Right.</param>
    /// <returns>Whether <paramref name="a"/> is stricter than <paramref name="b"/>.</returns>
    public static bool operator >(RatingScore a, RatingScore b) => a.CompareTo(b) > 0;

    /// <summary>Less-than.</summary>
    /// <param name="a">Left.</param>
    /// <param name="b">Right.</param>
    /// <returns>Whether <paramref name="a"/> is looser than <paramref name="b"/>.</returns>
    public static bool operator <(RatingScore a, RatingScore b) => a.CompareTo(b) < 0;

    /// <summary>Greater-or-equal.</summary>
    /// <param name="a">Left.</param>
    /// <param name="b">Right.</param>
    /// <returns>Whether <paramref name="a"/> is at least as strict as <paramref name="b"/>.</returns>
    public static bool operator >=(RatingScore a, RatingScore b) => a.CompareTo(b) >= 0;

    /// <summary>Less-or-equal.</summary>
    /// <param name="a">Left.</param>
    /// <param name="b">Right.</param>
    /// <returns>Whether <paramref name="a"/> is at most as strict as <paramref name="b"/>.</returns>
    public static bool operator <=(RatingScore a, RatingScore b) => a.CompareTo(b) <= 0;

    /// <inheritdoc />
    public override string ToString() => SubScore is null ? $"{Score}" : $"{Score}.{SubScore}";
}

/// <summary>Where a candidate rating came from.</summary>
public enum CandidateKind
{
    /// <summary>A user rule (tag, genre or collection).</summary>
    Rule,

    /// <summary>A built-in or configured mapping for a legacy rating string Jellyfin can't score.</summary>
    Legacy,

    /// <summary>A rating board's certification (any country).</summary>
    Certification,

    /// <summary>A Common Sense Media advisory age.</summary>
    CommonSense,
}

/// <summary>A raw rating reported by a source, before it's scored.</summary>
/// <param name="Source">Source name, e.g. <c>TMDb</c>.</param>
/// <param name="Kind">Certification or Common Sense age.</param>
/// <param name="Country">ISO 3166-1 country for a certification; null for an age.</param>
/// <param name="Value">The certification string, or the age as a string.</param>
public sealed record RawRating(string Source, CandidateKind Kind, string? Country, string Value);

/// <summary>A scored candidate for an item's rating.</summary>
/// <param name="Kind">What produced it.</param>
/// <param name="Origin">Human-readable origin, e.g. <c>TMDb GB "15"</c>.</param>
/// <param name="Score">Its position on the common scale.</param>
/// <param name="ExactRating">For rules and legacy mappings: the literal rating to write.</param>
public sealed record RatingCandidate(CandidateKind Kind, string Origin, RatingScore Score, string? ExactRating = null);

/// <summary>The fields of a library item that the engine looks at.</summary>
public sealed record ItemSnapshot
{
    /// <summary>Gets the Jellyfin item id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the item name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the production year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets a value indicating whether this is a series (else a movie).</summary>
    public bool IsSeries { get; init; }

    /// <summary>Gets the official (provider) rating.</summary>
    public string? OfficialRating { get; init; }

    /// <summary>Gets the custom rating.</summary>
    public string? CustomRating { get; init; }

    /// <summary>Gets the provider ids (Tmdb, Imdb, Tvdb, ...).</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets a value indicating whether the user locked the rating field.</summary>
    public bool RatingLocked { get; init; }

    /// <summary>Gets a value indicating whether the current custom rating was written by this plugin.</summary>
    public bool CustomRatingIsOurs { get; init; }

    /// <summary>Gets the TMDb id, if any.</summary>
    public string? TmdbId => GetProviderId("Tmdb");

    /// <summary>Gets a provider id, case-insensitively.</summary>
    /// <param name="provider">Provider name.</param>
    /// <returns>The id, or null.</returns>
    public string? GetProviderId(string provider)
    {
        foreach (var (key, value) in ProviderIds)
        {
            if (string.Equals(key, provider, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>The outcome for one item.</summary>
public enum DecisionKind
{
    /// <summary>Write a stricter Custom Rating.</summary>
    Raise,

    /// <summary>Write a looser Custom Rating (only when raise-only is turned off).</summary>
    Lower,

    /// <summary>The current rating already covers every candidate.</summary>
    Keep,

    /// <summary>No confident answer; a human should look.</summary>
    Review,

    /// <summary>Deliberately left alone (locked, or a manual custom rating).</summary>
    Skip,
}

/// <summary>What the engine decided for one item, and why.</summary>
public sealed record Decision
{
    /// <summary>Gets the item.</summary>
    public required ItemSnapshot Item { get; init; }

    /// <summary>Gets the outcome.</summary>
    public required DecisionKind Kind { get; init; }

    /// <summary>Gets the current effective rating (custom, else official).</summary>
    public string? CurrentRating { get; init; }

    /// <summary>Gets the current effective score, or null when unrated / unscored.</summary>
    public RatingScore? CurrentScore { get; init; }

    /// <summary>Gets the rating to write, for <see cref="DecisionKind.Raise"/>.</summary>
    public string? NewRating { get; init; }

    /// <summary>Gets the score of <see cref="NewRating"/>.</summary>
    public RatingScore? NewScore { get; init; }

    /// <summary>Gets a one-line explanation.</summary>
    public required string Reason { get; init; }

    /// <summary>Gets every candidate considered.</summary>
    public IReadOnlyList<RatingCandidate> Candidates { get; init; } = [];
}
