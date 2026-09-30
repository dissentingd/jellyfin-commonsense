using Jellyfin.Plugin.RatingsFixer.Configuration;
using Jellyfin.Plugin.RatingsFixer.Ledger;
using Jellyfin.Plugin.RatingsFixer.Rating;
using Jellyfin.Plugin.RatingsFixer.Service;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.RatingsFixer.Tests;

[Collection(JellyfinStaticsCollection.Name)]
public class ApplyTests : ServiceTestBase
{
    [Fact]
    public async Task Apply_WritesTheCustomRating_AndTheScoreJellyfinEnforces()
    {
        var movie = Library.AddMovie("Old Film", official: "Approved");
        Tmdb.Answer(movie, Cert("DE", "16"));

        var report = await ApplyAsync();

        Assert.Equal("R", movie.CustomRating);
        Assert.Equal("Approved", movie.OfficialRating);

        // The bug found live: without this, the rating is stored but not enforced.
        Assert.Equal(17, movie.InheritedParentalRatingValue);
        Assert.Same(movie, Assert.Single(Library.Saved));

        Assert.Equal(1, report.Counts["Raise"]);
        Assert.True(Assert.Single(report.Entries).Applied);

        var entry = Assert.Single(RatingLedger.Load(Service.LedgerPath).Entries);
        Assert.Equal(("R", "Approved", "1"), (entry.CustomRating, entry.PreviousOfficialRating, entry.ProviderIds["Tmdb"]));
    }

    [Fact]
    public async Task Preview_WritesNothing()
    {
        var movie = Library.AddMovie("Old Film", official: "Approved");
        Tmdb.Answer(movie, Cert("DE", "16"));

        var report = await PreviewAsync();

        Assert.Empty(Library.Saved);
        Assert.Null(movie.CustomRating);
        var entry = Assert.Single(report.Entries);
        Assert.Equal(("R", false), (entry.New, entry.Applied));
        Assert.Empty(RatingLedger.Load(Service.LedgerPath).Entries);
    }

    [Fact]
    public async Task RaiseOnly_NeverLowers()
    {
        var movie = Library.AddMovie("Film", official: "R");
        Tmdb.Answer(movie, Cert("GB", "PG"), Cert("US", "PG"));

        var report = await ApplyAsync();

        Assert.Equal(1, report.Counts["Keep"]);
        Assert.Empty(Library.Saved);
    }

    [Fact]
    public async Task HandSetCustomRating_IsSkipped_AndNotLookedUp()
    {
        var movie = Library.AddMovie("Film", official: "G", custom: "PG");
        Tmdb.Answer(movie, Cert("DE", "16"));

        var report = await ApplyAsync();

        Assert.Equal(1, report.Counts["Skip"]);
        Assert.Equal("PG", movie.CustomRating);
        Assert.Empty(Library.Saved);
        Assert.DoesNotContain(movie.Id, Tmdb.Asked);
    }

    [Fact]
    public async Task SecondApply_RecognisesItsOwnRatings()
    {
        var movie = Library.AddMovie("Old Film", official: "Approved");
        Tmdb.Answer(movie, Cert("DE", "16"));
        await ApplyAsync();

        var second = await ApplyAsync();

        Assert.Equal((0, 1, 0), (second.Counts["Raise"], second.Counts["Keep"], second.Counts["Skip"]));
        Assert.Single(Library.Saved);
    }

    [Fact]
    public async Task StaleEnforcedScore_OnOurRating_IsRepaired()
    {
        var movie = Library.AddMovie("Old Film", official: "Approved");
        Tmdb.Answer(movie, Cert("DE", "16"));
        await ApplyAsync();

        // As left by builds before the enforcement fix: rating written, score not.
        movie.InheritedParentalRatingValue = 0;
        var report = await ApplyAsync();

        Assert.Equal(1, report.Repaired);
        Assert.Equal(17, movie.InheritedParentalRatingValue);
        Assert.Equal(2, Library.Saved.Count);
    }

    [Fact]
    public async Task TagRule_GivesXxx_WithoutAskingSources()
    {
        var movie = Library.AddMovie("Film", official: "R", tags: "Adult");
        Tmdb.Answer(movie, Cert("GB", "15"));
        Context.Configuration.Rules = [new UserRule { Match = RuleMatch.Tag, Value = "adult", Rating = "XXX" }];

        await ApplyAsync();

        Assert.Equal("XXX", movie.CustomRating);
        Assert.Equal(1000, movie.InheritedParentalRatingValue);
        Assert.DoesNotContain(movie.Id, Tmdb.Asked);
    }

    [Fact]
    public async Task CollectionRule_RatesItsMembers_AndOnlyThem()
    {
        var member = Library.AddMovie("Member", official: "R", tmdb: "1");
        var memberSeries = Library.AddSeries("Member show", official: "TV-MA", tmdb: "2");
        var outsider = Library.AddMovie("Outsider", official: "R", tmdb: "3");
        Library.AddCollection("Adult", member, memberSeries);
        Context.Configuration.Rules = [new UserRule { Match = RuleMatch.Collection, Value = "adult", Rating = "XXX" }];

        var report = await ApplyAsync();

        Assert.Equal(("XXX", 1000), (member.CustomRating, member.InheritedParentalRatingValue));
        Assert.Equal("XXX", memberSeries.CustomRating);
        Assert.Null(outsider.CustomRating);
        Assert.Contains("rule: collection", report.Entries.Single(e => e.ItemId == member.Id).Reason);
        Assert.DoesNotContain(member.Id, Tmdb.Asked);
        Assert.Contains(outsider.Id, Tmdb.Asked);
    }

    [Fact]
    public async Task CollectionRule_ForAMissingCollection_Warns()
    {
        Library.AddMovie("Film", official: "R");
        Context.Configuration.Rules = [new UserRule { Match = RuleMatch.Collection, Value = "Nope", Rating = "XXX" }];

        var report = await PreviewAsync();

        Assert.Contains("No collection named \"Nope\" was found.", report.Warnings);
    }

    [Fact]
    public async Task Rule_WithAnUnknownRating_Warns_AndIsSkipped()
    {
        var movie = Library.AddMovie("Film", official: "R", tags: "Adult");
        Context.Configuration.Rules = [new UserRule { Match = RuleMatch.Tag, Value = "Adult", Rating = "XX" }];

        var report = await ApplyAsync();

        Assert.Contains(report.Warnings, w => w.Contains("\"XX\" isn't a rating", StringComparison.Ordinal));
        Assert.Null(movie.CustomRating);
    }

    [Fact]
    public async Task MdblistCertification_IsIgnored()
    {
        // MDBList's "certification" carries other countries' ratings unlabelled, so it must not count.
        var movie = Library.AddMovie("Unrated Film");
        Mdblist.Answer(movie, new RawRating("MDBList", CandidateKind.Certification, "US", "NC-17"));

        var report = await ApplyAsync();

        Assert.Equal(1, report.Counts["Review"]);
        Assert.Empty(Library.Saved);
    }

    [Fact]
    public async Task CertificationFromAnUnlistedCountry_IsIgnored()
    {
        var movie = Library.AddMovie("Unrated Film");
        Tmdb.Answer(movie, Cert("DE", "16"));
        Context.Configuration.Countries = "US, GB";

        var report = await ApplyAsync();

        Assert.Equal(1, report.Counts["Review"]);
        Assert.Empty(Library.Saved);
    }

    [Fact]
    public async Task CommonSense_CanBeTurnedOff()
    {
        var movie = Library.AddMovie("Unrated Film");
        Mdblist.Answer(movie, CommonSenseAge(16));
        Context.Configuration.UseCommonSense = false;

        var report = await ApplyAsync();

        Assert.Equal(1, report.Counts["Review"]);
        Assert.Empty(Library.Saved);
    }

    [Fact]
    public async Task FailedWrite_IsReported_AndNotLedgered()
    {
        var failing = Library.AddMovie("Failing", official: "Approved", tmdb: "1");
        var fine = Library.AddMovie("Fine", official: "Approved", tmdb: "2");
        Tmdb.Answer(failing, Cert("DE", "16"));
        Tmdb.Answer(fine, Cert("DE", "16"));
        Library.FailOn = failing;

        var report = await ApplyAsync();

        var failed = report.Entries.Single(e => e.ItemId == failing.Id);
        Assert.Equal(("disk full", false), (failed.Error, failed.Applied));
        Assert.True(report.Entries.Single(e => e.ItemId == fine.Id).Applied);
        Assert.Equal(fine.Id, Assert.Single(RatingLedger.Load(Service.LedgerPath).Entries).ItemId);
    }

    [Fact]
    public async Task Report_IsSaved_AndReloads()
    {
        var movie = Library.AddMovie("Old Film", official: "Approved");
        Tmdb.Answer(movie, Cert("DE", "16"));

        var report = await PreviewAsync();

        var loaded = Service.LoadReport();
        Assert.NotNull(loaded);
        Assert.Equal(report.Counts, loaded.Counts);
        Assert.Equal(DecisionKind.Raise, Assert.Single(loaded.Entries).Kind);
    }
}

[Collection(JellyfinStaticsCollection.Name)]
public class SeriesTests : ServiceTestBase
{
    [Fact]
    public async Task SeriesRating_ReachesOnlyEpisodesThatNeedIt()
    {
        var series = Library.AddSeries("Show", official: "TV-PG");
        var looser = Library.AddEpisode(series, "Looser", official: "TV-PG");
        var alreadyStrict = Library.AddEpisode(series, "Already strict", official: "TV-MA");
        var handSet = Library.AddEpisode(series, "Hand-set", official: "TV-PG", custom: "TV-14");
        var unrated = Library.AddEpisode(series, "Unrated");
        Tmdb.Answer(series, Cert("GB", "18"));

        var report = await ApplyAsync();

        Assert.Equal("TV-MA", series.CustomRating);
        Assert.Equal(2, report.ChildrenUpdated);

        Assert.Equal(("TV-MA", 17, 1), (looser.CustomRating, looser.InheritedParentalRatingValue, looser.InheritedParentalRatingSubValue));
        Assert.Equal("TV-PG", looser.OfficialRating); // official ratings are never touched
        Assert.Equal("TV-MA", unrated.CustomRating);
        Assert.Null(alreadyStrict.CustomRating);
        Assert.Equal("TV-14", handSet.CustomRating);
        Assert.DoesNotContain(alreadyStrict, Library.Saved);
        Assert.DoesNotContain(handSet, Library.Saved);
    }

    [Fact]
    public async Task NewEpisode_GetsItsSeriesRating_OnTheNextRun()
    {
        var series = Library.AddSeries("Show", official: "TV-PG");
        Library.AddEpisode(series, "Pilot", official: "TV-PG");
        Tmdb.Answer(series, Cert("GB", "18"));
        await ApplyAsync();

        var later = Library.AddEpisode(series, "Added later", official: "TV-PG");
        var report = await ApplyAsync();

        Assert.Equal(1, report.ChildrenUpdated);
        Assert.Equal(("TV-MA", 17), (later.CustomRating, later.InheritedParentalRatingValue));
        Assert.Single(Library.Saved, i => i is Series); // the series itself isn't re-saved
    }

    [Fact]
    public async Task EpisodeCarryingThePreviousSeriesRating_IsUpdated()
    {
        // The series was rated TV-14 by an earlier run (so its episodes carry TV-14); now it's stricter.
        var series = Library.AddSeries("Show", official: "TV-PG", custom: "TV-14");
        var episode = Library.AddEpisode(series, "Pilot", official: "TV-PG", custom: "TV-14");
        var ledger = RatingLedger.Load(Service.LedgerPath);
        ledger.Upsert(new LedgerEntry { ItemId = series.Id, Name = "Show", IsSeries = true, CustomRating = "TV-14" });
        ledger.Save();
        Tmdb.Answer(series, Cert("GB", "18"));

        await ApplyAsync();

        Assert.Equal("TV-MA", series.CustomRating);
        Assert.Equal("TV-MA", episode.CustomRating);
    }
}

[Collection(JellyfinStaticsCollection.Name)]
public class RestoreTests : ServiceTestBase
{
    [Fact]
    public async Task Restore_MatchesByProviderId_WhenItemIdsChanged()
    {
        var movie = Library.AddMovie("Film", official: "PG", tmdb: "77");
        LedgerEntry[] entries =
        [
            new() { ItemId = Guid.NewGuid(), Name = "Film", CustomRating = "R", ProviderIds = { ["Tmdb"] = "77" } },
            new() { ItemId = Guid.NewGuid(), Name = "Gone", Year = 1999, CustomRating = "R", ProviderIds = { ["Tmdb"] = "999" } },
        ];

        var dry = await Service.RestoreAsync(entries, apply: false, CancellationToken.None);
        Assert.Equal(1, dry.Changed);
        Assert.Empty(Library.Saved);

        var done = await Service.RestoreAsync(entries, apply: true, CancellationToken.None);

        Assert.Equal(("R", 17), (movie.CustomRating, movie.InheritedParentalRatingValue));
        Assert.Equal(["Gone (1999)"], done.Unmatched);
        Assert.Contains(RatingLedger.Load(Service.LedgerPath).Entries, e => e.ItemId == movie.Id && e.CustomRating == "R");
    }

    [Fact]
    public async Task Restore_OfASeries_ReachesItsEpisodes()
    {
        var series = Library.AddSeries("Show", official: "TV-PG", tmdb: "5");
        var episode = Library.AddEpisode(series, "Pilot", official: "TV-PG");
        LedgerEntry[] entries = [new() { ItemId = Guid.NewGuid(), Name = "Show", IsSeries = true, CustomRating = "TV-MA", ProviderIds = { ["Tmdb"] = "5" } }];

        var report = await Service.RestoreAsync(entries, apply: true, CancellationToken.None);

        Assert.Equal(1, report.ChildrenUpdated);
        Assert.Equal("TV-MA", episode.CustomRating);
    }
}

public class AutoRateTests
{
    private static readonly Guid SeriesId = Guid.NewGuid();

    [Fact]
    public void NewEpisodeOrSeason_QueuesItsSeries()
    {
        Assert.Equal(SeriesId, AutoRateService.QueueTarget(new Episode { SeriesId = SeriesId }, added: true, ItemUpdateType.None));
        Assert.Equal(SeriesId, AutoRateService.QueueTarget(new Season { SeriesId = SeriesId }, added: true, ItemUpdateType.None));
    }

    [Fact]
    public void MovieOrSeries_IsQueuedOnceItsMetadataIsDownloaded()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        Assert.Null(AutoRateService.QueueTarget(movie, added: true, ItemUpdateType.None));
        Assert.Equal(movie.Id, AutoRateService.QueueTarget(movie, added: false, ItemUpdateType.MetadataDownload));
        Assert.Equal(movie.Id, AutoRateService.QueueTarget(movie, added: false, ItemUpdateType.MetadataDownload | ItemUpdateType.ImageUpdate));
    }

    [Fact]
    public void OurOwnWrites_AndOtherUpdates_AreIgnored()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        Assert.Null(AutoRateService.QueueTarget(movie, added: false, ItemUpdateType.MetadataEdit));
        Assert.Null(AutoRateService.QueueTarget(new Episode { SeriesId = SeriesId }, added: false, ItemUpdateType.MetadataDownload));
        Assert.Null(AutoRateService.QueueTarget(new Episode { SeriesId = SeriesId, IsVirtualItem = true }, added: true, ItemUpdateType.None));
    }
}
