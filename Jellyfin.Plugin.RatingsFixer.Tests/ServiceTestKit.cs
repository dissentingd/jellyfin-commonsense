using Jellyfin.Data.Enums;
using Jellyfin.Plugin.RatingsFixer.Configuration;
using Jellyfin.Plugin.RatingsFixer.Rating;
using Jellyfin.Plugin.RatingsFixer.Service;
using Jellyfin.Plugin.RatingsFixer.Sources;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.RatingsFixer.Tests;

/// <summary>
/// Service tests share Jellyfin's static services (<see cref="BaseItem.LibraryManager"/> and friends),
/// so they run one at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class JellyfinStaticsCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "Jellyfin statics";
}

/// <summary>
/// An in-memory library behind a mocked <see cref="ILibraryManager"/>: answers the queries the plugin
/// makes and records every save, so tests can assert on exactly what was written.
/// </summary>
internal sealed class FakeLibrary
{
    private readonly Dictionary<Guid, Guid> _seriesOf = [];
    private readonly Dictionary<Guid, Guid> _libraryOf = [];

    public FakeLibrary()
    {
        Mock.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) => Query(q));
        Mock.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => Items.FirstOrDefault(i => i.Id == id)!);
        Mock.Setup(m => m.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
        Mock.Setup(m => m.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions());
        Mock.Setup(m => m.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Returns((BaseItem item, BaseItem _, ItemUpdateType _, CancellationToken _) => Save([item]));
        Mock.Setup(m => m.UpdateItemsAsync(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<BaseItem> items, BaseItem _, ItemUpdateType _, CancellationToken _) => Save(items));
    }

    public Mock<ILibraryManager> Mock { get; } = new();

    public List<BaseItem> Items { get; } = [];

    /// <summary>Gets every item saved, in order (one entry per save of an item).</summary>
    public List<BaseItem> Saved { get; } = [];

    /// <summary>Gets or sets an item whose save should fail.</summary>
    public BaseItem? FailOn { get; set; }

    /// <summary>Puts items into a library (a recursive ParentId query on it returns them).</summary>
    public void PutInLibrary(Guid library, params BaseItem[] items)
    {
        foreach (var item in items)
        {
            _libraryOf[item.Id] = library;
        }
    }

    public Movie AddMovie(string name, string? official = null, string? custom = null, string? tmdb = "1", params string[] tags)
    {
        var movie = Init(new Movie(), name, official, custom, tmdb);
        movie.Tags = tags;
        Items.Add(movie);
        return movie;
    }

    public Series AddSeries(string name, string? official = null, string? custom = null, string? tmdb = "2")
    {
        var series = Init(new Series(), name, official, custom, tmdb);
        Items.Add(series);
        return series;
    }

    public BoxSet AddCollection(string name, params BaseItem[] members)
    {
        var collection = new BoxSet
        {
            Id = Guid.NewGuid(),
            Name = name,
            LinkedChildren = [.. members.Select(m => new LinkedChild { ItemId = m.Id, Type = LinkedChildType.Manual })],
        };
        Items.Add(collection);
        return collection;
    }

    public Episode AddEpisode(Series series, string name, string? official = null, string? custom = null)
    {
        var episode = Init(new Episode(), name, official, custom, tmdb: null);
        episode.SeriesId = series.Id;
        _seriesOf[episode.Id] = series.Id;
        Items.Add(episode);
        return episode;
    }

    private static T Init<T>(T item, string name, string? official, string? custom, string? tmdb)
        where T : BaseItem
    {
        item.Id = Guid.NewGuid();
        item.Name = name;
        item.OfficialRating = official;
        item.CustomRating = custom;
        item.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tmdb is not null)
        {
            item.ProviderIds["Tmdb"] = tmdb;
        }

        item.LockedFields = [];
        item.Tags = [];
        item.Genres = [];

        // As Jellyfin stores it after a metadata refresh.
        item.OnMetadataChanged();
        return item;
    }

    private static BaseItemKind KindOf(BaseItem item) => item switch
    {
        Movie => BaseItemKind.Movie,
        Series => BaseItemKind.Series,
        Season => BaseItemKind.Season,
        Episode => BaseItemKind.Episode,
        BoxSet => BaseItemKind.BoxSet,
        _ => BaseItemKind.Folder,
    };

    private List<BaseItem> Query(InternalItemsQuery q)
    {
        IEnumerable<BaseItem> result = Items;
        if (q.IncludeItemTypes.Length > 0)
        {
            result = result.Where(i => q.IncludeItemTypes.Contains(KindOf(i)));
        }

        if (q.ItemIds.Length > 0)
        {
            result = result.Where(i => q.ItemIds.Contains(i.Id));
        }

        if (q.ParentId != Guid.Empty)
        {
            result = result.Where(i => _libraryOf.TryGetValue(i.Id, out var l) && l == q.ParentId);
        }

        if (q.AncestorIds.Length > 0)
        {
            result = result.Where(i => _seriesOf.TryGetValue(i.Id, out var s) && q.AncestorIds.Contains(s));
        }

        return result.ToList();
    }

    private Task Save(IEnumerable<BaseItem> items)
    {
        foreach (var item in items)
        {
            if (ReferenceEquals(item, FailOn))
            {
                throw new IOException("disk full");
            }

            Saved.Add(item);
        }

        return Task.CompletedTask;
    }
}

/// <summary>A rating source that answers from a dictionary and records what it was asked.</summary>
internal sealed class FakeSource(string name) : IRatingSource
{
    public string Name => name;

    public Dictionary<Guid, List<RawRating>> Answers { get; } = [];

    public List<Guid> Asked { get; } = [];

    /// <summary>Gets the cache age the last run allowed (zero means "ask afresh").</summary>
    public TimeSpan? LastMaxAge { get; private set; }

    public void Answer(BaseItem item, params RawRating[] ratings) => Answers[item.Id] = [.. ratings];

    public Task<Dictionary<Guid, List<RawRating>>> FetchAsync(IReadOnlyList<ItemSnapshot> items, SourceRun run, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Asked.AddRange(items.Select(i => i.Id));
        LastMaxAge = run.MaxAge;
        return Task.FromResult(items.Where(i => Answers.ContainsKey(i.Id)).ToDictionary(i => i.Id, i => Answers[i.Id]));
    }
}

/// <summary>Resolves TMDb ids from a dictionary of IMDb ids, recording what it was asked.</summary>
internal sealed class FakeResolver : ITmdbIdResolver
{
    public Dictionary<string, string> ByImdb { get; } = [];

    public List<Guid> Asked { get; } = [];

    public Task<Dictionary<Guid, string>> ResolveAsync(IReadOnlyList<ItemSnapshot> items, string apiKey, IdMapCache cache, TimeSpan retryAfter, CancellationToken cancellationToken)
    {
        Asked.AddRange(items.Select(i => i.Id));
        return Task.FromResult(items
            .Where(i => i.GetProviderId("Imdb") is { } imdb && ByImdb.ContainsKey(imdb))
            .ToDictionary(i => i.Id, i => ByImdb[i.GetProviderId("Imdb")!]));
    }
}

/// <summary>Settings and a throwaway data folder.</summary>
internal sealed class TestContext : IPluginContext, IDisposable
{
    public PluginConfiguration Configuration { get; } = new() { TmdbApiKey = "test", MdblistApiKey = "test", TvdbApiKey = "test" };

    public string DataFolderPath { get; } = Path.Combine(Path.GetTempPath(), "rf-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(DataFolderPath))
        {
            Directory.Delete(DataFolderPath, recursive: true);
        }
    }
}

/// <summary>Base for service tests: a fresh library, sources, context and service per test.</summary>
public abstract class ServiceTestBase : IDisposable
{
    private static readonly FakeScale Scale = new();

    internal ServiceTestBase()
    {
        // BaseItem computes rating scores through these statics.
        var localization = new Mock<ILocalizationManager>();
        localization.Setup(l => l.GetRatingScore(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string rating, string country) => Scale.Score(rating, country) is { } s ? new ParentalRatingScore(s.Score, s.SubScore) : null!);
        var config = new Mock<IServerConfigurationManager>();
        config.Setup(c => c.Configuration).Returns(new ServerConfiguration { MetadataCountryCode = "US" });
        BaseItem.LocalizationManager = localization.Object;
        BaseItem.ConfigurationManager = config.Object;
        BaseItem.LibraryManager = Library.Mock.Object;

        Service = new RatingsFixerService(Library.Mock.Object, Scale, [Tmdb, Mdblist, Tvdb], Context, NullLogger<RatingsFixerService>.Instance, Resolver);
    }

    internal FakeLibrary Library { get; } = new();

    internal FakeSource Tmdb { get; } = new("TMDb");

    internal FakeSource Mdblist { get; } = new("MDBList");

    internal FakeSource Tvdb { get; } = new("TVDb");

    internal FakeResolver Resolver { get; } = new();

    internal TestContext Context { get; } = new();

    internal RatingsFixerService Service { get; }

    public void Dispose()
    {
        Context.Dispose();
        GC.SuppressFinalize(this);
    }

    internal static RawRating Cert(string country, string value) => new("TMDb", CandidateKind.Certification, country, value);

    internal static RawRating CommonSenseAge(int age) => new("MDBList", CandidateKind.CommonSense, null, age.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal Task<RunReport> ApplyAsync() => Service.RunAsync(apply: true, onlyItems: null, saveReport: true, progress: null, CancellationToken.None);

    internal Task<RunReport> PreviewAsync() => Service.RunAsync(apply: false, onlyItems: null, saveReport: true, progress: null, CancellationToken.None);
}
