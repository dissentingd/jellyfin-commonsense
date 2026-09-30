using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Jellyfin.Plugin.RatingsFixer.Rating;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RatingsFixer.Sources;

/// <summary>A source gave up for this run (bad key, daily limit, ...). Other sources carry on.</summary>
/// <param name="message">What went wrong, in words a user can act on.</param>
public sealed class SourceUnavailableException(string message) : Exception(message);

/// <summary>Everything a source needs for one run.</summary>
/// <remarks>Sources return and cache everything they get; filtering by country or kind happens later,
/// so changing those settings never leaves a stale cache.</remarks>
/// <param name="ApiKey">The source's API key.</param>
/// <param name="Cache">This source's cache.</param>
/// <param name="MaxAge">How old a cache entry may be.</param>
/// <param name="Warnings">Where to report problems that only cut the run short.</param>
/// <param name="Pin">A second credential some sources need (TVDb's subscriber PIN).</param>
public sealed record SourceRun(string ApiKey, SourceCache Cache, TimeSpan MaxAge, List<string> Warnings, string? Pin = null);

/// <summary>Finds TMDb ids for items that only have other providers' ids.</summary>
public interface ITmdbIdResolver
{
    /// <summary>Looks up TMDb ids from IMDb ids (and TVDb ids for series).</summary>
    /// <param name="items">Items without a TMDb id.</param>
    /// <param name="apiKey">TMDb API key.</param>
    /// <param name="cache">The id map.</param>
    /// <param name="retryAfter">How long a "not found" answer is trusted.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>TMDb ids by item id, for the items that could be resolved.</returns>
    /// <exception cref="SourceUnavailableException">TMDb can't be used this run.</exception>
    Task<Dictionary<Guid, string>> ResolveAsync(IReadOnlyList<ItemSnapshot> items, string apiKey, IdMapCache cache, TimeSpan retryAfter, CancellationToken cancellationToken);
}

/// <summary>A place to look up ratings by TMDb id.</summary>
public interface IRatingSource
{
    /// <summary>Gets the source's display name (also its cache file name).</summary>
    string Name { get; }

    /// <summary>Fetches ratings for items that have a TMDb id.</summary>
    /// <param name="items">Items to look up.</param>
    /// <param name="run">Per-run settings.</param>
    /// <param name="progress">Progress, 0–100.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Ratings per item id. Items the source couldn't answer are absent.</returns>
    /// <exception cref="SourceUnavailableException">The source can't be used this run.</exception>
    Task<Dictionary<Guid, List<RawRating>>> FetchAsync(IReadOnlyList<ItemSnapshot> items, SourceRun run, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>TMDb certifications for every configured country.</summary>
/// <param name="httpClientFactory">HTTP client factory.</param>
/// <param name="logger">Logger.</param>
public sealed class TmdbSource(IHttpClientFactory httpClientFactory, ILogger<TmdbSource> logger) : IRatingSource, ITmdbIdResolver
{
    private const string BaseUrl = "https://api.themoviedb.org/3/";

    /// <inheritdoc />
    public string Name => "TMDb";

    /// <inheritdoc />
    public async Task<Dictionary<Guid, List<RawRating>>> FetchAsync(IReadOnlyList<ItemSnapshot> items, SourceRun run, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<RawRating>>();
        var todo = new List<ItemSnapshot>();
        foreach (var item in items)
        {
            if (item.TmdbId is null)
            {
                continue;
            }

            if (run.Cache.TryGet(Key(item), run.MaxAge, out var cached))
            {
                result[item.Id] = cached;
            }
            else
            {
                todo.Add(item);
            }
        }

        if (todo.Count == 0)
        {
            return result;
        }

        using var http = httpClientFactory.CreateClient(Plugin.HttpClientName);
        var done = 0;
        var gate = new Lock();
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (item, ct) =>
        {
            var path = item.IsSeries ? $"tv/{item.TmdbId}/content_ratings" : $"movie/{item.TmdbId}/release_dates";
            var body = await GetAsync(http, path, run.ApiKey, ct).ConfigureAwait(false);
            var ratings = body is null
                ? []
                : item.IsSeries ? SourceParsers.ParseTmdbSeries(body, []) : SourceParsers.ParseTmdbMovie(body, []);
            run.Cache.Set(Key(item), ratings);
            bool checkpoint;
            lock (gate)
            {
                result[item.Id] = ratings;
                progress?.Report(100.0 * ++done / todo.Count);
                checkpoint = done % 500 == 0;
            }

            if (checkpoint)
            {
                // A restart mid-run shouldn't throw away thousands of lookups.
                run.Cache.Save();
            }
        }).ConfigureAwait(false);

        return result;
    }

    /// <inheritdoc />
    public async Task<Dictionary<Guid, string>> ResolveAsync(IReadOnlyList<ItemSnapshot> items, string apiKey, IdMapCache cache, TimeSpan retryAfter, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        using var http = httpClientFactory.CreateClient(Plugin.HttpClientName);
        var gate = new Lock();
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (item, ct) =>
        {
            // IMDb first; series can also be found by their TVDb id.
            var lookups = new List<(string Source, string Id)>();
            if (item.GetProviderId("Imdb") is { } imdb)
            {
                lookups.Add(("imdb_id", imdb));
            }

            if (item.IsSeries && item.GetProviderId("Tvdb") is { } tvdb)
            {
                lookups.Add(("tvdb_id", tvdb));
            }

            foreach (var (source, id) in lookups)
            {
                var key = $"{source}:{id}:{(item.IsSeries ? "tv" : "movie")}";
                if (!cache.TryGet(key, retryAfter, out var tmdbId))
                {
                    var body = await GetAsync(http, $"find/{Uri.EscapeDataString(id)}?external_source={source}", apiKey, ct).ConfigureAwait(false);
                    tmdbId = body is null ? null : SourceParsers.ParseTmdbFind(body, item.IsSeries);
                    cache.Set(key, tmdbId);
                }

                if (tmdbId is not null)
                {
                    lock (gate)
                    {
                        result[item.Id] = tmdbId;
                    }

                    break;
                }
            }
        }).ConfigureAwait(false);

        return result;
    }

    private static string Key(ItemSnapshot item) => $"{(item.IsSeries ? "tv" : "movie")}:{item.TmdbId}";

    private async Task<string?> GetAsync(HttpClient http, string path, string apiKey, CancellationToken ct)
    {
        // A v4 "read access token" is a JWT and goes in a header; a v3 key goes in the query.
        var isToken = apiKey.StartsWith("eyJ", StringComparison.Ordinal);
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = BaseUrl + path + (isToken ? string.Empty : separator + "api_key=" + Uri.EscapeDataString(apiKey));
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (isToken)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                case HttpStatusCode.NotFound:
                    return null;
                case HttpStatusCode.Unauthorized:
                    throw new SourceUnavailableException("TMDb rejected the API key — check it on the plugin settings page.");
                case HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError when attempt < 4:
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    logger.LogDebug("TMDb returned {Status} for {Path}; retrying in {Wait}", (int)response.StatusCode, path, wait);
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                default:
                    throw new SourceUnavailableException($"TMDb returned HTTP {(int)response.StatusCode} for {path}.");
            }
        }
    }
}

/// <summary>MDBList: Common Sense Media ages, fetched in batches.</summary>
/// <param name="httpClientFactory">HTTP client factory.</param>
/// <param name="logger">Logger.</param>
public sealed class MdblistSource(IHttpClientFactory httpClientFactory, ILogger<MdblistSource> logger) : IRatingSource
{
    private const string BaseUrl = "https://api.mdblist.com/tmdb/";
    private const int BatchSize = 100;

    /// <inheritdoc />
    public string Name => "MDBList";

    /// <inheritdoc />
    public async Task<Dictionary<Guid, List<RawRating>>> FetchAsync(IReadOnlyList<ItemSnapshot> items, SourceRun run, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<RawRating>>();
        var todo = new List<ItemSnapshot>();
        foreach (var item in items)
        {
            if (item.TmdbId is null)
            {
                continue;
            }

            if (run.Cache.TryGet(Key(item), run.MaxAge, out var cached))
            {
                result[item.Id] = cached;
            }
            else
            {
                todo.Add(item);
            }
        }

        using var http = httpClientFactory.CreateClient(Plugin.HttpClientName);
        var batches = todo.GroupBy(i => i.IsSeries).SelectMany(g => g.Chunk(BatchSize)).ToList();
        for (var b = 0; b < batches.Count; b++)
        {
            var batch = batches[b];
            var type = batch[0].IsSeries ? "show" : "movie";
            var ids = batch.Select(i => long.TryParse(i.TmdbId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? (object)n : i.TmdbId!).ToArray();
            var url = $"{BaseUrl}{type}/?apikey={Uri.EscapeDataString(run.ApiKey)}";

            using var response = await http.PostAsJsonAsync(url, new { ids }, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new SourceUnavailableException("MDBList rejected the API key — check it on the plugin settings page.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Keep what we have; the cache means the next run picks up where this one stopped.
                run.Warnings.Add($"MDBList's request limit was reached after {b} of {batches.Count} batches; the rest will be fetched on a later run.");
                break;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SourceUnavailableException($"MDBList returned HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = SourceParsers.ParseMdblist(body, useCertification: false, useCommonSense: true);
            foreach (var item in batch)
            {
                var ratings = parsed.TryGetValue(item.TmdbId!, out var r) ? r : [];
                run.Cache.Set(Key(item), ratings);
                result[item.Id] = ratings;
            }

            run.Cache.Save();
            logger.LogDebug("MDBList batch {Batch}/{Total}: {Count} items answered", b + 1, batches.Count, parsed.Count);
            progress?.Report(100.0 * (b + 1) / batches.Count);
        }

        return result;
    }

    private static string Key(ItemSnapshot item) => $"{(item.IsSeries ? "show" : "movie")}:{item.TmdbId}";
}

/// <summary>TVDb content ratings for series (TVDb v4 API; needs a project key, plus a PIN for user-supported keys).</summary>
/// <param name="httpClientFactory">HTTP client factory.</param>
/// <param name="logger">Logger.</param>
public sealed class TvdbSource(IHttpClientFactory httpClientFactory, ILogger<TvdbSource> logger) : IRatingSource
{
    private const string BaseUrl = "https://api4.thetvdb.com/v4/";
    private readonly SemaphoreSlim _login = new(1, 1);
    private string? _token;
    private string? _tokenFor;

    /// <inheritdoc />
    public string Name => "TVDb";

    /// <inheritdoc />
    public async Task<Dictionary<Guid, List<RawRating>>> FetchAsync(IReadOnlyList<ItemSnapshot> items, SourceRun run, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<RawRating>>();
        var todo = new List<(ItemSnapshot Item, string TvdbId)>();
        foreach (var item in items)
        {
            if (!item.IsSeries || item.GetProviderId("Tvdb") is not { } tvdbId)
            {
                continue;
            }

            if (run.Cache.TryGet($"series:{tvdbId}", run.MaxAge, out var cached))
            {
                result[item.Id] = cached;
            }
            else
            {
                todo.Add((item, tvdbId));
            }
        }

        if (todo.Count == 0)
        {
            return result;
        }

        using var http = httpClientFactory.CreateClient(Plugin.HttpClientName);
        var done = 0;
        var gate = new Lock();
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (work, ct) =>
        {
            var body = await GetSeriesAsync(http, work.TvdbId, run, ct).ConfigureAwait(false);
            var ratings = body is null ? [] : SourceParsers.ParseTvdbSeries(body);
            run.Cache.Set($"series:{work.TvdbId}", ratings);
            bool checkpoint;
            lock (gate)
            {
                result[work.Item.Id] = ratings;
                progress?.Report(100.0 * ++done / todo.Count);
                checkpoint = done % 500 == 0;
            }

            if (checkpoint)
            {
                run.Cache.Save();
            }
        }).ConfigureAwait(false);

        return result;
    }

    private async Task<string?> GetSeriesAsync(HttpClient http, string tvdbId, SourceRun run, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetTokenAsync(http, run, forceNew: attempt > 0, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}series/{Uri.EscapeDataString(tvdbId)}/extended?short=true");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                case HttpStatusCode.NotFound:
                    return null;
                case HttpStatusCode.Unauthorized when attempt == 0:
                    continue; // token expired: log in again once
                case HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError when attempt < 4:
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    logger.LogDebug("TVDb returned {Status} for series {Id}; retrying in {Wait}", (int)response.StatusCode, tvdbId, wait);
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                default:
                    throw new SourceUnavailableException($"TVDb returned HTTP {(int)response.StatusCode} for series {tvdbId}.");
            }
        }
    }

    private async Task<string> GetTokenAsync(HttpClient http, SourceRun run, bool forceNew, CancellationToken ct)
    {
        var credentials = run.ApiKey + "|" + run.Pin;
        await _login.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceNew && _token is not null && _tokenFor == credentials)
            {
                return _token;
            }

            object body = string.IsNullOrWhiteSpace(run.Pin) ? new { apikey = run.ApiKey } : new { apikey = run.ApiKey, pin = run.Pin };
            using var response = await http.PostAsJsonAsync(BaseUrl + "login", body, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new SourceUnavailableException("TVDb rejected the API key or PIN - check them on the plugin settings page.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SourceUnavailableException($"TVDb login returned HTTP {(int)response.StatusCode}.");
            }

            using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            _token = doc.RootElement.GetProperty("data").GetProperty("token").GetString()
                ?? throw new SourceUnavailableException("TVDb login returned no token.");
            _tokenFor = credentials;
            return _token;
        }
        finally
        {
            _login.Release();
        }
    }
}
