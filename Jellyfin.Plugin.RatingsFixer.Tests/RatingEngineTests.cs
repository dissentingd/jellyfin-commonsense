using Jellyfin.Plugin.RatingsFixer.Configuration;
using Jellyfin.Plugin.RatingsFixer.Rating;
using Jellyfin.Plugin.RatingsFixer.Service;

namespace Jellyfin.Plugin.RatingsFixer.Tests;

/// <summary>A cut-down copy of Jellyfin 12.1's rating tables — enough to exercise the engine.</summary>
internal sealed class FakeScale(string defaultCountry = "US") : IRatingScale
{
    private static readonly Dictionary<string, Dictionary<string, RatingScore>> Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Approved"] = new(0), ["G"] = new(0), ["PG"] = new(10), ["PG-13"] = new(13), ["R"] = new(17), ["NC-17"] = new(17, 1),
            ["TV-Y"] = new(0), ["TV-Y7"] = new(7), ["TV-G"] = new(0), ["TV-PG"] = new(10), ["TV-14"] = new(14), ["TV-MA"] = new(17, 1),
            ["XXX"] = new(1000),
        },
        ["GB"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["U"] = new(0), ["PG"] = new(8), ["12A"] = new(12), ["12"] = new(12, 1), ["15"] = new(15, 3), ["18"] = new(18, 1), ["R18"] = new(1000),
        },
        ["AU"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["G"] = new(0), ["PG"] = new(15, 1), ["M"] = new(15, 2), ["MA15+"] = new(15, 3), ["R18+"] = new(18, 1),
        },
        ["DE"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["0"] = new(0), ["6"] = new(6), ["12"] = new(12), ["16"] = new(16), ["18"] = new(18),
        },
    };

    public RatingScore? Score(string rating, string? country = null) =>
        Tables.TryGetValue(country ?? defaultCountry, out var table) && table.TryGetValue(rating, out var score) ? score : null;
}

public class RatingEngineTests
{
    private static readonly EngineOptions Defaults = RatingsFixerService.ToEngineOptions(new PluginConfiguration());

    private static readonly EngineOptions StrictestRoundUp = Defaults with { Combine = CombineMode.Strictest, RoundToNearest = false };

    private static RatingEngine Engine(EngineOptions? options = null) => new(new FakeScale(), options ?? Defaults);

    private static ItemSnapshot Movie(string? official = null, string? custom = null, bool series = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test",
        OfficialRating = official,
        CustomRating = custom,
        IsSeries = series,
    };

    private static RawRating Cert(string country, string value) => new("TMDb", CandidateKind.Certification, country, value);

    private static RawRating Age(int age) => new("MDBList", CandidateKind.CommonSense, null, age.ToString());

    [Fact]
    public void OldApprovedFilm_ReRatedFifteenInBritain_RoundsToNearest()
    {
        // 15 sits exactly between PG-13 (13) and R (17); a tie goes to the looser step.
        var d = Engine().Decide(Movie("Approved"), [], [Cert("GB", "15"), Cert("US", "Approved")]);
        Assert.Equal(DecisionKind.Raise, d.Kind);
        Assert.Equal("PG-13", d.NewRating);
    }

    [Fact]
    public void RoundUp_SendsFifteenToR()
    {
        var d = Engine(StrictestRoundUp).Decide(Movie("Approved"), [], [Cert("GB", "15")]);
        Assert.Equal("R", d.NewRating);
    }

    [Fact]
    public void Nearest_SendsSixteenToR_AndTwelveToPg13()
    {
        Assert.Equal("R", Engine().Decide(Movie(null), [], [Cert("DE", "16")]).NewRating);
        Assert.Equal("PG-13", Engine().Decide(Movie(null), [], [Cert("GB", "12A")]).NewRating);
    }

    [Fact]
    public void Median_IgnoresOneStrictOutlier()
    {
        var d = Engine().Decide(Movie("PG-13"), [], [Cert("US", "PG-13"), Cert("GB", "12A"), Cert("DE", "12"), Age(18)]);
        Assert.Equal(DecisionKind.Keep, d.Kind);
        Assert.Contains("consensus of 4", d.Reason);
    }

    [Fact]
    public void CurrentRatingAlreadyStrictEnough_IsKept()
    {
        var d = Engine().Decide(Movie("PG-13"), [], [Cert("GB", "12A"), Cert("DE", "12")]);
        Assert.Equal(DecisionKind.Keep, d.Kind);
        Assert.Null(d.NewRating);
    }

    [Fact]
    public void StrictestMode_StrictestSourceWins()
    {
        var d = Engine(Defaults with { Combine = CombineMode.Strictest }).Decide(Movie("PG"), [], [Cert("GB", "12A"), Cert("DE", "16"), Age(13)]);
        Assert.Equal("R", d.NewRating);
        Assert.Contains("DE", d.Reason);
    }

    [Fact]
    public void RuleBeatsEverySource()
    {
        var engine = Engine();
        var rule = engine.RuleCandidate("rule: collection \"Adult\" → XXX", "XXX")!;
        var d = engine.Decide(Movie("R"), [rule], [Cert("GB", "15")]);
        Assert.Equal(DecisionKind.Raise, d.Kind);
        Assert.Equal("XXX", d.NewRating);
        Assert.Equal(new RatingScore(1000), d.NewScore);
    }

    [Fact]
    public void RuleWithUnknownRating_IsRejected()
    {
        Assert.Null(Engine().RuleCandidate("rule", "NOT-A-RATING"));
    }

    [Fact]
    public void RaiseOnly_NeverLowers()
    {
        var d = Engine().Decide(Movie("R"), [], [Cert("GB", "PG")]);
        Assert.Equal(DecisionKind.Keep, d.Kind);
    }

    [Fact]
    public void WithoutRaiseOnly_CanLower()
    {
        var d = Engine(Defaults with { RaiseOnly = false }).Decide(Movie("R"), [], [Cert("GB", "PG")]);
        Assert.Equal(DecisionKind.Lower, d.Kind);
        Assert.Equal("PG", d.NewRating);
    }

    [Fact]
    public void Unrated_WithNoSources_GoesToReview()
    {
        var d = Engine().Decide(Movie(null), [], []);
        Assert.Equal(DecisionKind.Review, d.Kind);
    }

    [Fact]
    public void Approved_WithNoSources_GoesToReview()
    {
        var d = Engine().Decide(Movie("Approved"), [], []);
        Assert.Equal(DecisionKind.Review, d.Kind);
    }

    [Fact]
    public void SourceSayingApproved_IsNotTakenAsG()
    {
        // "Approved" is a pre-1968 seal, not an age rating: an unrated film it's the only answer for must stay on
        // the review list rather than become G (visible to every account).
        Assert.Equal(DecisionKind.Review, Engine().Decide(Movie(null), [], [Cert("US", "Approved")]).Kind);
        Assert.Equal(DecisionKind.Review, Engine().Decide(Movie("Approved"), [], [Cert("US", "Approved")]).Kind);
    }

    [Fact]
    public void SourceSayingApproved_DoesNotDragTheConsensusDown()
    {
        // With a real rating alongside, "Approved" simply doesn't count.
        var d = Engine().Decide(Movie("Approved"), [], [Cert("US", "Approved"), Cert("DE", "16")]);
        Assert.Equal("R", d.NewRating);
    }

    [Fact]
    public void Scored_WithNoSources_IsKept()
    {
        Assert.Equal(DecisionKind.Keep, Engine().Decide(Movie("PG"), [], []).Kind);
    }

    [Fact]
    public void LegacyX_MapsToNc17()
    {
        var d = Engine().Decide(Movie("X"), [], []);
        Assert.Equal(DecisionKind.Raise, d.Kind);
        Assert.Equal("NC-17", d.NewRating);
    }

    [Fact]
    public void UnknownForeignCertification_IsIgnored()
    {
        var d = Engine().Decide(Movie(null), [], [Cert("GB", "banana")]);
        Assert.Equal(DecisionKind.Review, d.Kind);
    }

    [Fact]
    public void ManualCustomRating_IsSkipped_ButOursIsNot()
    {
        Assert.Equal(DecisionKind.Skip, Engine().Decide(Movie("G", custom: "PG"), [], [Cert("GB", "15")]).Kind);

        var ours = Movie("G", custom: "PG") with { CustomRatingIsOurs = true };
        var d = Engine().Decide(ours, [], [Cert("GB", "18")]);
        Assert.Equal(DecisionKind.Raise, d.Kind);
        Assert.Equal("PG", d.CurrentRating);
        Assert.Equal("R", d.NewRating);
    }

    [Fact]
    public void LockedRating_IsSkipped()
    {
        var d = Engine().Decide(Movie("G") with { RatingLocked = true }, [], [Cert("GB", "18")]);
        Assert.Equal(DecisionKind.Skip, d.Kind);
    }

    [Fact]
    public void CommonSenseAge_UsesTheLadderForTheType()
    {
        Assert.Equal(DecisionKind.Keep, Engine().Decide(Movie("PG-13"), [], [Age(14)]).Kind);
        Assert.Equal("R", Engine().Decide(Movie("PG-13"), [], [Age(16)]).NewRating);
        Assert.Equal("TV-14", Engine().Decide(Movie("TV-PG", series: true), [], [Age(14)]).NewRating);
    }

    [Fact]
    public void ForeignEighteen_BecomesR_NotNc17()
    {
        Assert.Equal(DecisionKind.Keep, Engine().Decide(Movie("R"), [], [Cert("GB", "18")]).Kind);
        Assert.Equal("R", Engine().Decide(Movie(null), [], [Cert("GB", "18")]).NewRating);
        Assert.Equal("R", Engine(StrictestRoundUp).Decide(Movie(null), [], [Cert("DE", "18")]).NewRating);
    }

    [Fact]
    public void ForeignEighteen_OnASeries_BecomesTvMa()
    {
        Assert.Equal("TV-MA", Engine().Decide(Movie("TV-14", series: true), [], [Cert("GB", "18")]).NewRating);
    }

    [Fact]
    public void ActualNc17Certification_IsKept()
    {
        var d = Engine().Decide(Movie("R"), [], [Cert("US", "NC-17")]);
        Assert.Equal(DecisionKind.Raise, d.Kind);
        Assert.Equal("NC-17", d.NewRating);
    }

    [Fact]
    public void ExactCertificationFromTheOtherLadder_IsRounded()
    {
        // A US "PG" (a movie-ladder rating) on a series maps onto the TV ladder instead of being written as-is.
        Assert.Equal("TV-PG", Engine().Decide(Movie(null, series: true), [], [Cert("US", "PG")]).NewRating);
    }

    [Fact]
    public void AdultCertificationAbroad_DoesNotBecomeXxxWithoutARule()
    {
        // XXX only ever comes from a user rule or a legacy mapping, never from rounding.
        Assert.Equal("R", Engine().Decide(Movie(null), [], [Cert("GB", "R18")]).NewRating);
    }

    [Fact]
    public void UnscoredLadderEntries_AreReported()
    {
        var engine = Engine(Defaults with { MovieLadder = ["G", "PG", "Typo", "R"] });
        Assert.Equal(["Typo"], engine.UnscoredLadderEntries);
    }

    [Fact]
    public void SharedScore_PicksTheClosestSubScore()
    {
        // Australia's PG, M and MA15+ all score 15: a BBFC 15 (15.3) must land on MA15+, not PG.
        var australia = new RatingEngine(new FakeScale("AU"), Defaults with { MovieLadder = ["G", "PG", "M", "MA15+", "R18+"] });

        Assert.Equal("MA15+", australia.Decide(Movie(null), [], [Cert("GB", "15")]).NewRating);
        Assert.Equal("R18+", australia.Decide(Movie(null), [], [Cert("GB", "18")]).NewRating);
    }

    [Fact]
    public void ConfigLists_Parse()
    {
        Assert.Equal(["US", "GB", "DE"], RatingsFixerService.SplitList(" US, GB ,\nDE,"));
        var map = RatingsFixerService.ParseMap("X=NC-17\r\n bad line \nAO = XXX\n=nothing");
        Assert.Equal(2, map.Count);
        Assert.Equal("XXX", map["ao"]);
    }
}
