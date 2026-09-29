using System.Globalization;

namespace Jellyfin.Plugin.CommonSense.Rating;

/// <summary>How the ratings from several sources become one.</summary>
public enum CombineMode
{
    /// <summary>The middle answer (upper median): one outlier country can't move a title alone.</summary>
    Median,

    /// <summary>The strictest answer: any one source can raise a title.</summary>
    Strictest,
}

/// <summary>Knobs for <see cref="RatingEngine"/>. Mirrors the relevant parts of the plugin configuration.</summary>
public sealed record EngineOptions
{
    /// <summary>Gets a value indicating whether ratings may only ever get stricter.</summary>
    public bool RaiseOnly { get; init; } = true;

    /// <summary>Gets a value indicating whether a Custom Rating someone set by hand is left alone.</summary>
    public bool RespectManualCustomRating { get; init; } = true;

    /// <summary>Gets a value indicating whether items with a locked rating field are left alone.</summary>
    public bool RespectLockedRating { get; init; } = true;

    /// <summary>Gets the ratings a movie may be given, loosest first.</summary>
    public IReadOnlyList<string> MovieLadder { get; init; } = [];

    /// <summary>Gets the ratings a series may be given, loosest first.</summary>
    public IReadOnlyList<string> SeriesLadder { get; init; } = [];

    /// <summary>Gets replacements for legacy rating strings Jellyfin can't score (e.g. <c>X</c> → <c>NC-17</c>).</summary>
    public IReadOnlyDictionary<string, string> LegacyMap { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets how source ratings are combined.</summary>
    public CombineMode Combine { get; init; } = CombineMode.Median;

    /// <summary>Gets a value indicating whether a score rounds to the nearest ladder step (else up).</summary>
    public bool RoundToNearest { get; init; } = true;

    /// <summary>Gets ratings that are scored but don't mean anything modern (e.g. <c>Approved</c>).</summary>
    public IReadOnlyCollection<string> UnverifiedRatings { get; init; } = [];
}

/// <summary>
/// Decides each item's rating from user rules and source ratings. Pure: no I/O,
/// no Jellyfin types, so every rule in docs/DESIGN.md is unit-tested directly.
/// </summary>
public sealed class RatingEngine
{
    private readonly IRatingScale _scale;
    private readonly EngineOptions _options;
    private readonly List<(string Rating, RatingScore Score)> _movieLadder;
    private readonly List<(string Rating, RatingScore Score)> _seriesLadder;

    /// <summary>Initializes a new instance of the <see cref="RatingEngine"/> class.</summary>
    /// <param name="scale">The rating scale.</param>
    /// <param name="options">The options.</param>
    public RatingEngine(IRatingScale scale, EngineOptions options)
    {
        _scale = scale;
        _options = options;
        _movieLadder = BuildLadder(options.MovieLadder);
        _seriesLadder = BuildLadder(options.SeriesLadder);
    }

    /// <summary>Gets ladder entries the server couldn't score (likely typos, or a different country's scale).</summary>
    public IReadOnlyList<string> UnscoredLadderEntries { get; private set; } = [];

    /// <summary>Scores a rule's target rating.</summary>
    /// <param name="origin">Description of the rule.</param>
    /// <param name="rating">The rating the rule assigns.</param>
    /// <returns>The candidate, or null when the server can't score the rating.</returns>
    public RatingCandidate? RuleCandidate(string origin, string rating)
    {
        var score = _scale.Score(rating);
        return score is null ? null : new RatingCandidate(CandidateKind.Rule, origin, score.Value, rating);
    }

    /// <summary>Scores a raw source rating on the common scale.</summary>
    /// <param name="raw">The raw rating.</param>
    /// <returns>The candidate, or null when it can't be scored.</returns>
    public RatingCandidate? Score(RawRating raw)
    {
        if (string.IsNullOrWhiteSpace(raw.Value))
        {
            return null;
        }

        if (raw.Kind == CandidateKind.CommonSense)
        {
            return int.TryParse(raw.Value.Trim().TrimEnd('+'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var age) && age > 0
                ? new RatingCandidate(CandidateKind.CommonSense, $"{raw.Source} Common Sense age {age}+", new RatingScore(age))
                : null;
        }

        var value = raw.Value.Trim();
        if (_scale.Score(value, raw.Country) is not { } score)
        {
            return null;
        }

        // A certification that is itself a step on our ladder, meaning the same thing here (e.g. TMDb
        // US "NC-17"), is used as-is rather than rounded — rounding never produces NC-17 on its own.
        var exact = IsLadderRating(value) && _scale.Score(value) == score ? value : null;
        return new RatingCandidate(raw.Kind, $"{raw.Source} {raw.Country} \"{value}\"", score, exact);
    }

    /// <summary>Decides one item.</summary>
    /// <param name="item">The item.</param>
    /// <param name="ruleCandidates">Candidates from user rules that matched this item.</param>
    /// <param name="sourceRatings">Raw ratings from every enabled source.</param>
    /// <returns>The decision.</returns>
    public Decision Decide(ItemSnapshot item, IReadOnlyList<RatingCandidate> ruleCandidates, IReadOnlyList<RawRating> sourceRatings)
    {
        var current = FirstNonBlank(item.CustomRating, item.OfficialRating);
        var currentScore = current is null ? null : _scale.Score(current);

        Decision Make(DecisionKind kind, string reason, IReadOnlyList<RatingCandidate>? candidates = null, string? newRating = null, RatingScore? newScore = null) => new()
        {
            Item = item,
            Kind = kind,
            CurrentRating = current,
            CurrentScore = currentScore,
            NewRating = newRating,
            NewScore = newScore,
            Reason = reason,
            Candidates = candidates ?? [],
        };

        if (_options.RespectLockedRating && item.RatingLocked)
        {
            return Make(DecisionKind.Skip, "rating field is locked");
        }

        if (_options.RespectManualCustomRating && !string.IsNullOrWhiteSpace(item.CustomRating) && !item.CustomRatingIsOurs)
        {
            return Make(DecisionKind.Skip, $"has a manually set Custom Rating \"{item.CustomRating}\"");
        }

        List<RatingCandidate> candidates;
        if (ruleCandidates.Count > 0)
        {
            // User rules are the user's own curation: they win outright over any source.
            candidates = [.. ruleCandidates];
        }
        else
        {
            candidates = [];
            var official = item.OfficialRating?.Trim();
            if (!string.IsNullOrEmpty(official)
                && _scale.Score(official) is null
                && TryGetLegacy(official, out var mapped)
                && _scale.Score(mapped) is { } mappedScore)
            {
                candidates.Add(new RatingCandidate(CandidateKind.Legacy, $"legacy \"{official}\" → {mapped}", mappedScore, mapped));
            }

            foreach (var raw in sourceRatings)
            {
                if (Score(raw) is { } candidate)
                {
                    candidates.Add(candidate);
                }
            }
        }

        if (candidates.Count == 0)
        {
            if (currentScore is null)
            {
                var what = current is null ? "unrated" : $"\"{current}\" isn't a rating Jellyfin can score";
                return Make(DecisionKind.Review, $"{what}, and no source had a usable rating");
            }

            if (IsUnverified(current!))
            {
                return Make(DecisionKind.Review, $"\"{current}\" predates modern ratings, and no source had a modern rating");
            }

            return Make(DecisionKind.Keep, "no source had a rating; keeping the current one");
        }

        var (chosen, how) = Choose(candidates, fromRules: ruleCandidates.Count > 0);
        string? newRating;
        RatingScore newScore;
        var ladder = item.IsSeries ? _seriesLadder : _movieLadder;
        if (chosen.ExactRating is not null
            && (chosen.Kind is CandidateKind.Rule or CandidateKind.Legacy
                || ladder.Any(s => string.Equals(s.Rating, chosen.ExactRating, StringComparison.OrdinalIgnoreCase))))
        {
            newRating = chosen.ExactRating;
            newScore = chosen.Score;
        }
        else if (MapToLadder(ladder, chosen.Score, _options.RoundToNearest) is { } step)
        {
            (newRating, newScore) = step;
        }
        else
        {
            return Make(DecisionKind.Review, $"no {(item.IsSeries ? "series" : "movie")} rating ladder is configured", candidates);
        }

        if (currentScore is { } cur)
        {
            if (newScore <= cur && (_options.RaiseOnly || newScore == cur || string.Equals(newRating, current, StringComparison.OrdinalIgnoreCase)))
            {
                return Make(DecisionKind.Keep, $"\"{current}\" already covers the {how} ({chosen.Origin})", candidates);
            }
        }

        var verb = currentScore is { } c2 && newScore < c2 ? "lower" : "raise";
        return Make(
            verb == "raise" ? DecisionKind.Raise : DecisionKind.Lower,
            $"{verb} to {newRating}: {how} is {chosen.Origin}",
            candidates,
            newRating,
            newScore);
    }

    /// <summary>
    /// Maps a score onto a ladder. Board ratings are compared on the whole score only, and never land
    /// on a sub-scored step when a plain step shares its score — so a foreign 18 becomes <c>R</c>, not
    /// <c>NC-17</c> (while a series still reaches <c>TV-MA</c>, the only 17 on its ladder).
    /// </summary>
    private static (string Rating, RatingScore Score)? MapToLadder(List<(string Rating, RatingScore Score)> ladder, RatingScore needed, bool nearest)
    {
        var steps = ladder
            .Where(s => (s.Score.SubScore ?? 0) == 0 || !ladder.Any(o => o.Score.Score == s.Score.Score && (o.Score.SubScore ?? 0) == 0))
            .ToList();
        if (steps.Count == 0)
        {
            return null;
        }

        var target = needed.Score;
        if (!nearest)
        {
            return steps.FirstOrDefault(s => s.Score.Score >= target) is { Rating: not null } up ? up : steps[^1];
        }

        // Nearest step; a tie goes to the looser one (15 sits between PG-13 and R → PG-13).
        var best = steps[0];
        foreach (var step in steps)
        {
            if (Math.Abs(step.Score.Score - target) < Math.Abs(best.Score.Score - target))
            {
                best = step;
            }
        }

        return best;
    }

    private (RatingCandidate Chosen, string How) Choose(List<RatingCandidate> candidates, bool fromRules)
    {
        var ordered = candidates.OrderBy(c => c.Score).ToList();
        if (fromRules || ordered.Count == 1 || _options.Combine == CombineMode.Strictest)
        {
            return (ordered[^1], fromRules ? "strictest matching rule" : "strictest source");
        }

        // Upper median: with an even count, lean strict.
        return (ordered[ordered.Count / 2], $"consensus of {ordered.Count} sources");
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private List<(string Rating, RatingScore Score)> BuildLadder(IReadOnlyList<string> ratings)
    {
        var ladder = new List<(string, RatingScore)>();
        var unscored = new List<string>(UnscoredLadderEntries);
        foreach (var rating in ratings)
        {
            if (_scale.Score(rating) is { } score)
            {
                ladder.Add((rating, score));
            }
            else
            {
                unscored.Add(rating);
            }
        }

        UnscoredLadderEntries = unscored;
        return [.. ladder.OrderBy(s => s.Item2)];
    }

    private bool IsLadderRating(string rating) =>
        _movieLadder.Any(s => string.Equals(s.Rating, rating, StringComparison.OrdinalIgnoreCase))
        || _seriesLadder.Any(s => string.Equals(s.Rating, rating, StringComparison.OrdinalIgnoreCase));

    private bool TryGetLegacy(string rating, out string mapped)
    {
        foreach (var (key, value) in _options.LegacyMap)
        {
            if (string.Equals(key, rating, StringComparison.OrdinalIgnoreCase))
            {
                mapped = value;
                return true;
            }
        }

        mapped = string.Empty;
        return false;
    }

    private bool IsUnverified(string rating) =>
        _options.UnverifiedRatings.Any(r => string.Equals(r, rating, StringComparison.OrdinalIgnoreCase));
}
