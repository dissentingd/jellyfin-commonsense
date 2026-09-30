using Jellyfin.Plugin.RatingsFixer.Ledger;
using Jellyfin.Plugin.RatingsFixer.Rating;
using Jellyfin.Plugin.RatingsFixer.Service;
using Jellyfin.Plugin.RatingsFixer.Sources;

namespace Jellyfin.Plugin.RatingsFixer.Tests;

public class SourceParserTests
{
    private const string TmdbMovie = """
        {"id":1,"results":[
          {"iso_3166_1":"GB","release_dates":[
            {"certification":"A","release_date":"1943-01-01T00:00:00.000Z","type":3},
            {"certification":"PG","release_date":"2012-11-30T00:00:00.000Z","type":3},
            {"certification":"","release_date":"2020-01-01T00:00:00.000Z","type":4}]},
          {"iso_3166_1":"US","release_dates":[{"certification":"Approved","release_date":"1943-01-23T00:00:00.000Z","type":3}]},
          {"iso_3166_1":"FR","release_dates":[{"certification":"U","release_date":"1947-01-01T00:00:00.000Z","type":3}]}
        ]}
        """;

    [Fact]
    public void TmdbMovie_TakesMostRecentCertificationPerCountry()
    {
        var ratings = SourceParsers.ParseTmdbMovie(TmdbMovie, []);
        Assert.Equal(3, ratings.Count);
        Assert.Equal("PG", ratings.Single(r => r.Country == "GB").Value);
    }

    [Fact]
    public void TmdbMovie_FiltersCountries()
    {
        var ratings = SourceParsers.ParseTmdbMovie(TmdbMovie, ["us", "GB"]);
        Assert.Equal(["GB", "US"], ratings.Select(r => r.Country!).Order());
    }

    [Fact]
    public void TmdbSeries_Parses()
    {
        var ratings = SourceParsers.ParseTmdbSeries(
            """{"results":[{"iso_3166_1":"US","rating":"TV-14"},{"iso_3166_1":"DE","rating":""}]}""", []);
        var only = Assert.Single(ratings);
        Assert.Equal(new RawRating("TMDb", CandidateKind.Certification, "US", "TV-14"), only);
    }

    [Fact]
    public void Mdblist_BatchWithMixedIdShapes()
    {
        var json = """
            [
              {"title":"A","ids":{"tmdb":11,"imdb":"tt1"},"certification":"PG","commonsense":true,"age_rating":10},
              {"title":"B","tmdbid":"22","certification":"R","commonsense":false,"age_rating":16},
              {"title":"C","id":33,"certification":null,"commonsense":null}
            ]
            """;
        var parsed = SourceParsers.ParseMdblist(json, useCertification: true, useCommonSense: true);

        Assert.Equal(3, parsed.Count);
        Assert.Contains(parsed["11"], r => r.Kind == CandidateKind.CommonSense && r.Value == "10");
        Assert.DoesNotContain(parsed["22"], r => r.Kind == CandidateKind.CommonSense);
        Assert.Contains(parsed["22"], r => r.Kind == CandidateKind.Certification && r.Country == "US" && r.Value == "R");
        Assert.Empty(parsed["33"]);
    }

    [Fact]
    public void TvdbSeries_MapsThreeLetterCountries_AndDropsUnknownOnes()
    {
        var json = """
            {"status":"success","data":{"id":1,"name":"Show","contentRatings":[
              {"id":1,"name":"TV-MA","country":"usa","contentType":""},
              {"id":2,"name":"15","country":"gbr","contentType":""},
              {"id":3,"name":"TV-14","country":"usa","contentType":"episode"},
              {"id":4,"name":"K-16","country":"xyz","contentType":""},
              {"id":5,"name":"","country":"deu","contentType":""}]}}
            """;

        var ratings = SourceParsers.ParseTvdbSeries(json);

        Assert.Equal(
            [new RawRating("TVDb", CandidateKind.Certification, "US", "TV-MA"), new RawRating("TVDb", CandidateKind.Certification, "GB", "15")],
            ratings);
    }

    [Fact]
    public void TvdbSeries_WithoutRatings_IsEmpty()
    {
        Assert.Empty(SourceParsers.ParseTvdbSeries("""{"status":"success","data":{"id":1,"name":"Show"}}"""));
    }

    [Fact]
    public void TmdbFind_ReadsTheRightResultList()
    {
        var json = """{"movie_results":[{"id":603}],"tv_results":[{"id":1399}],"person_results":[]}""";
        Assert.Equal("603", SourceParsers.ParseTmdbFind(json, series: false));
        Assert.Equal("1399", SourceParsers.ParseTmdbFind(json, series: true));
        Assert.Null(SourceParsers.ParseTmdbFind("""{"movie_results":[]}""", series: false));
    }

    [Fact]
    public void Mdblist_SingleObject()
    {
        var parsed = SourceParsers.ParseMdblist("""{"tmdbid":5,"commonsense":1,"age_rating":"13+"}""", true, true);
        Assert.Equal("13", Assert.Single(parsed["5"]).Value);
    }
}

public class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cs-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Ledger_RoundTrips_AndUpsertsById()
    {
        var path = Path.Combine(_dir, "ledger.json");
        var ledger = RatingLedger.Load(path);
        var id = Guid.NewGuid();
        ledger.Upsert(new LedgerEntry { ItemId = id, Name = "A", CustomRating = "PG-13", ProviderIds = { ["Tmdb"] = "1" } });
        ledger.Upsert(new LedgerEntry { ItemId = id, Name = "A", CustomRating = "R", ProviderIds = { ["Tmdb"] = "1" } });
        ledger.Save();

        var reloaded = RatingLedger.Load(path);
        var entry = Assert.Single(reloaded.Entries);
        Assert.Equal("R", entry.CustomRating);
        Assert.Equal("1", entry.ProviderIds["tmdb"]);
    }

    [Fact]
    public void Ledger_KeepsTheOriginalPreviousRating_AcrossRepeatedWrites()
    {
        var ledger = RatingLedger.Load(Path.Combine(_dir, "ledger.json"));
        var id = Guid.NewGuid();
        ledger.Upsert(new LedgerEntry { ItemId = id, CustomRating = "PG-13", PreviousCustomRating = null, PreviousOfficialRating = "Approved" });

        // A later run raises it again: its "previous" is our own PG-13, which mustn't become the revert target.
        ledger.Upsert(new LedgerEntry { ItemId = id, CustomRating = "R", PreviousCustomRating = "PG-13", PreviousOfficialRating = "Approved" });

        var entry = ledger.Find(id)!;
        Assert.Equal(("R", null, "Approved"), (entry.CustomRating, entry.PreviousCustomRating, entry.PreviousOfficialRating));
    }

    [Fact]
    public void Exclusions_MatchById_OrBySharedProviderId()
    {
        var path = Path.Combine(_dir, "exclusions.json");
        var list = ExclusionList.Load(path);
        var id = Guid.NewGuid();
        list.Add(new ExclusionEntry { ItemId = id, Name = "Film", ProviderIds = { ["Tmdb"] = "42" } });
        list.Save();

        var reloaded = ExclusionList.Load(path);
        Assert.True(reloaded.Contains(id, false, new Dictionary<string, string>()));
        Assert.True(reloaded.Contains(Guid.NewGuid(), false, new Dictionary<string, string> { ["tmdb"] = "42" }));
        Assert.False(reloaded.Contains(Guid.NewGuid(), true, new Dictionary<string, string> { ["Tmdb"] = "42" })); // a series isn't the movie
        Assert.True(reloaded.Remove(id));
    }

    [Fact]
    public void Report_SerializesKindsAsStrings()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new RunReport { Entries = [new ReportEntry { Kind = DecisionKind.Review }] },
            RatingLedger.JsonOptions);
        Assert.Contains("\"Review\"", json);
    }

    [Fact]
    public void Cache_RespectsMaxAge_AndPersists()
    {
        var path = Path.Combine(_dir, "cache.json");
        var cache = SourceCache.Load(path);
        cache.Set("movie:1", [new RawRating("TMDb", CandidateKind.Certification, "GB", "15")]);
        cache.Set("movie:2", []);
        cache.Save();

        var reloaded = SourceCache.Load(path);
        Assert.True(reloaded.TryGet("movie:1", TimeSpan.FromDays(1), out var one));
        Assert.Equal("15", Assert.Single(one).Value);
        Assert.True(reloaded.TryGet("movie:2", TimeSpan.FromDays(1), out var two));
        Assert.Empty(two);
        Assert.False(reloaded.TryGet("movie:1", TimeSpan.FromTicks(-1), out _));
        Assert.False(reloaded.TryGet("movie:3", TimeSpan.FromDays(1), out _));
    }

    [Fact]
    public void IdMap_KeepsFoundIds_AndRetriesMissesLater()
    {
        var path = Path.Combine(_dir, "ids.json");
        var cache = IdMapCache.Load(path);
        cache.Set("imdb_id:tt1:movie", "603");
        cache.Set("imdb_id:tt2:movie", null);
        cache.Save();

        var reloaded = IdMapCache.Load(path);
        Assert.True(reloaded.TryGet("imdb_id:tt1:movie", TimeSpan.Zero, out var found));
        Assert.Equal("603", found);
        Assert.True(reloaded.TryGet("imdb_id:tt2:movie", TimeSpan.FromDays(1), out var missing));
        Assert.Null(missing);
        Assert.False(reloaded.TryGet("imdb_id:tt2:movie", TimeSpan.FromTicks(-1), out _));
    }

    [Fact]
    public void Cache_CorruptFile_StartsEmpty()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "cache.json");
        File.WriteAllText(path, "{not json");
        Assert.False(SourceCache.Load(path).TryGet("x", TimeSpan.MaxValue, out _));
    }
}
