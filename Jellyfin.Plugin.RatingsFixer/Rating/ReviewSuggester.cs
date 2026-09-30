namespace Jellyfin.Plugin.RatingsFixer.Rating;

/// <summary>What the suggester knows about a title on the review list.</summary>
public sealed record ReviewFacts
{
    /// <summary>Gets a value indicating whether the title is a series.</summary>
    public bool IsSeries { get; init; }

    /// <summary>Gets the production year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the genres.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Gets the tags (TMDb keywords, for most libraries).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Gets the effective ratings of other titles in the same collections, with the collection name.</summary>
    public IReadOnlyList<(string Collection, string Rating)> SiblingRatings { get; init; } = [];
}

/// <summary>A suggested rating and why.</summary>
/// <param name="Rating">The suggested rating, already on the title's ladder.</param>
/// <param name="Reasons">Every signal that contributed, strictest first.</param>
public sealed record ReviewSuggestion(string Rating, IReadOnlyList<string> Reasons);

/// <summary>Settings for <see cref="ReviewSuggester"/>.</summary>
public sealed record SuggesterOptions
{
    /// <summary>Gets keyword fragments that suggest mature content (matched case-insensitively inside tags).</summary>
    public IReadOnlyList<string> MatureKeywords { get; init; } = [];

    /// <summary>Gets the rating mature keywords suggest.</summary>
    public string MatureRating { get; init; } = "R";

    /// <summary>Gets genre → rating defaults.</summary>
    public IReadOnlyDictionary<string, string> GenreRatings { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets a value indicating whether ratings of other titles in the same collection count.</summary>
    public bool UseCollections { get; init; } = true;

    /// <summary>Gets tag → rating era hints (e.g. <c>pre-code</c> → <c>PG-13</c>).</summary>
    public IReadOnlyDictionary<string, string> EraTags { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Suggests ratings for review-list titles from their other metadata. Every signal proposes a rating; the
/// strictest wins, so a suggestion errs on the safe side. Suggestions are only ever shown for the admin to
/// accept — never written on their own. Pure (no Jellyfin types), so it's unit-tested directly.
/// </summary>
public sealed class ReviewSuggester(IRatingScale scale, RatingEngine engine, SuggesterOptions options)
{
    /// <summary>Suggests a rating, or returns null when no signal applies.</summary>
    /// <param name="facts">What's known about the title.</param>
    /// <returns>The suggestion, or null.</returns>
    public ReviewSuggestion? Suggest(ReviewFacts facts)
    {
        var signals = new List<(RatingScore Score, string Reason)>();

        void Add(string rating, string reason)
        {
            if (scale.Score(rating) is { } score)
            {
                signals.Add((score, reason));
            }
        }

        var mature = facts.Tags
            .Where(t => options.MatureKeywords.Any(k => t.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (mature.Count > 0)
        {
            Add(options.MatureRating, $"keywords: {string.Join(", ", mature.Take(4))}{(mature.Count > 4 ? ", …" : string.Empty)}");
        }

        foreach (var genre in facts.Genres)
        {
            if (Lookup(options.GenreRatings, genre) is { } rating)
            {
                Add(rating, $"genre {genre} → {rating}");
            }
        }

        foreach (var tag in facts.Tags)
        {
            if (Lookup(options.EraTags, tag) is { } rating)
            {
                Add(rating, $"{tag} → {rating}");
            }
        }

        if (options.UseCollections)
        {
            // The strictest sibling in each collection: sequels and series entries usually share a rating.
            foreach (var group in facts.SiblingRatings.GroupBy(s => s.Collection, StringComparer.OrdinalIgnoreCase))
            {
                // Siblings can all be unscorable too (e.g. every entry "NR"): then this collection says nothing.
                var scored = group
                    .Select(s => (s.Rating, Score: scale.Score(s.Rating)))
                    .Where(s => s.Score is not null)
                    .ToList();
                if (scored.Count > 0)
                {
                    var strictest = scored.MaxBy(s => s.Score!.Value);
                    Add(strictest.Rating, $"collection “{group.Key}” has {strictest.Rating}");
                }
            }
        }

        if (signals.Count == 0)
        {
            return null;
        }

        var ordered = signals.OrderByDescending(s => s.Score).ToList();
        return engine.RatingFor(ordered[0].Score, facts.IsSeries) is { } onLadder
            ? new ReviewSuggestion(onLadder, [.. ordered.Select(s => s.Reason)])
            : null;
    }

    private static string? Lookup(IReadOnlyDictionary<string, string> map, string key)
    {
        foreach (var (k, v) in map)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }
}
