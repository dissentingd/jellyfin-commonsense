using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.RatingsFixer.Rating;

namespace Jellyfin.Plugin.RatingsFixer.Sources;

/// <summary>Turns source API responses into <see cref="RawRating"/>s. Pure, for unit tests.</summary>
public static class SourceParsers
{
    /// <summary>
    /// Parses TMDb <c>/movie/{id}/release_dates</c>. For each wanted country, takes the
    /// certification of the most recent release that has one — re-releases are exactly
    /// where old films get re-rated.
    /// </summary>
    /// <param name="json">Response body.</param>
    /// <param name="countries">Countries to keep.</param>
    /// <returns>One rating per country that has a certification.</returns>
    public static List<RawRating> ParseTmdbMovie(string json, IReadOnlyCollection<string> countries)
    {
        var result = new List<RawRating>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var country in results.EnumerateArray())
        {
            var code = GetString(country, "iso_3166_1");
            if (code is null || !Wanted(countries, code) || !country.TryGetProperty("release_dates", out var dates))
            {
                continue;
            }

            string? best = null;
            var bestDate = DateTime.MinValue;
            foreach (var release in dates.EnumerateArray())
            {
                var cert = GetString(release, "certification")?.Trim();
                if (string.IsNullOrEmpty(cert))
                {
                    continue;
                }

                var date = DateTime.TryParse(GetString(release, "release_date"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d)
                    ? d
                    : DateTime.MinValue;
                if (best is null || date > bestDate)
                {
                    best = cert;
                    bestDate = date;
                }
            }

            if (best is not null)
            {
                result.Add(new RawRating("TMDb", CandidateKind.Certification, code, best));
            }
        }

        return result;
    }

    /// <summary>Parses TMDb <c>/tv/{id}/content_ratings</c>.</summary>
    /// <param name="json">Response body.</param>
    /// <param name="countries">Countries to keep.</param>
    /// <returns>One rating per country that has one.</returns>
    public static List<RawRating> ParseTmdbSeries(string json, IReadOnlyCollection<string> countries)
    {
        var result = new List<RawRating>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var entry in results.EnumerateArray())
        {
            var code = GetString(entry, "iso_3166_1");
            var rating = GetString(entry, "rating")?.Trim();
            if (code is not null && !string.IsNullOrEmpty(rating) && Wanted(countries, code))
            {
                result.Add(new RawRating("TMDb", CandidateKind.Certification, code, rating));
            }
        }

        return result;
    }

    /// <summary>
    /// Parses an MDBList item or batch response (<c>/tmdb/{movie|show}/</c>) into ratings keyed by TMDb id.
    /// Reads, when <c>commonsense</c> is set, <c>age_rating</c> as the Common Sense Media age.
    /// MDBList's <c>certification</c> is <b>not</b> reliably US (live data has <c>U</c>, <c>15</c>, <c>12</c>, …
    /// from other countries' releases), so the plugin doesn't use it; TMDb covers certifications.
    /// </summary>
    /// <param name="json">Response body: one object or an array of them.</param>
    /// <param name="useCertification">Whether to keep MDBList's certification (country unknown; tagged <c>US</c>).</param>
    /// <param name="useCommonSense">Whether to keep the Common Sense age.</param>
    /// <returns>Ratings by TMDb id.</returns>
    public static Dictionary<string, List<RawRating>> ParseMdblist(string json, bool useCertification, bool useCommonSense)
    {
        var result = new Dictionary<string, List<RawRating>>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        IEnumerable<JsonElement> items = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray()
            : [doc.RootElement];

        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var tmdbId = GetTmdbId(item);
            if (tmdbId is null)
            {
                continue;
            }

            var ratings = new List<RawRating>();
            var cert = GetString(item, "certification")?.Trim();
            if (useCertification && !string.IsNullOrEmpty(cert))
            {
                ratings.Add(new RawRating("MDBList", CandidateKind.Certification, "US", cert));
            }

            if (useCommonSense && GetCommonSenseAge(item) is { } age)
            {
                ratings.Add(new RawRating("MDBList", CandidateKind.CommonSense, null, age.ToString(CultureInfo.InvariantCulture)));
            }

            result[tmdbId] = ratings;
        }

        return result;
    }

    /// <summary>
    /// Parses TVDb v4 <c>/series/{id}/extended</c>: every <c>contentRatings</c> entry, with TVDb's
    /// three-letter country codes (<c>usa</c>, <c>gbr</c>) turned into the two-letter ones used elsewhere.
    /// Countries without a known mapping are dropped.
    /// </summary>
    /// <param name="json">Response body.</param>
    /// <returns>One rating per country.</returns>
    public static List<RawRating> ParseTvdbSeries(string json)
    {
        var result = new List<RawRating>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("contentRatings", out var ratings)
            || ratings.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var entry in ratings.EnumerateArray())
        {
            var name = GetString(entry, "name")?.Trim();
            var country = GetString(entry, "country")?.Trim();
            if (string.IsNullOrEmpty(name) || country is null || !Alpha3ToAlpha2.TryGetValue(country, out var code))
            {
                continue;
            }

            if (!result.Any(r => r.Country == code))
            {
                result.Add(new RawRating("TVDb", CandidateKind.Certification, code, name));
            }
        }

        return result;
    }

    /// <summary>Parses TMDb <c>/find/{external_id}</c>: the TMDb id of the first movie or TV result.</summary>
    /// <param name="json">Response body.</param>
    /// <param name="series">Whether to read TV results (else movie results).</param>
    /// <returns>The TMDb id, or null.</returns>
    public static string? ParseTmdbFind(string json, bool series)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(series ? "tv_results" : "movie_results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var result in results.EnumerateArray())
        {
            if (GetIdString(result, "id") is { } id)
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses an OMDb <c>?i={imdb id}</c> response: IMDb's US rating (<c>Rated</c>). <c>N/A</c> means no rating.
    /// </summary>
    /// <param name="json">Response body.</param>
    /// <returns>The outcome.</returns>
    public static OmdbResult ParseOmdb(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!string.Equals(GetString(root, "Response"), "True", StringComparison.OrdinalIgnoreCase))
        {
            var error = GetString(root, "Error") ?? string.Empty;
            if (error.Contains("limit", StringComparison.OrdinalIgnoreCase))
            {
                return new OmdbResult(OmdbStatus.LimitReached, []);
            }

            return error.Contains("API key", StringComparison.OrdinalIgnoreCase)
                ? new OmdbResult(OmdbStatus.BadKey, [])
                : new OmdbResult(OmdbStatus.Ok, []); // not found, incorrect id: an answer, just an empty one
        }

        var rated = GetString(root, "Rated")?.Trim();
        return string.IsNullOrEmpty(rated) || string.Equals(rated, "N/A", StringComparison.OrdinalIgnoreCase)
            ? new OmdbResult(OmdbStatus.Ok, [])
            : new OmdbResult(OmdbStatus.Ok, [new RawRating("OMDb", CandidateKind.Certification, "US", rated)]);
    }

    /// <summary>TVDb's ISO 3166-1 alpha-3 codes for the countries Jellyfin has rating tables for.</summary>
    private static readonly Dictionary<string, string> Alpha3ToAlpha2 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arg"] = "AR", ["aus"] = "AU", ["aut"] = "AT", ["bel"] = "BE", ["bgr"] = "BG", ["bra"] = "BR",
        ["can"] = "CA", ["che"] = "CH", ["chl"] = "CL", ["col"] = "CO", ["cze"] = "CZ", ["deu"] = "DE",
        ["dnk"] = "DK", ["esp"] = "ES", ["fin"] = "FI", ["fra"] = "FR", ["gbr"] = "GB", ["grc"] = "GR",
        ["hun"] = "HU", ["idn"] = "ID", ["ind"] = "IN", ["irl"] = "IE", ["ita"] = "IT", ["jpn"] = "JP",
        ["kaz"] = "KZ", ["kor"] = "KR", ["ltu"] = "LT", ["mex"] = "MX", ["nld"] = "NL", ["nor"] = "NO",
        ["nzl"] = "NZ", ["phl"] = "PH", ["pol"] = "PL", ["prt"] = "PT", ["rou"] = "RO", ["rus"] = "RU",
        ["sgp"] = "SG", ["svk"] = "SK", ["swe"] = "SE", ["tha"] = "TH", ["tur"] = "TR", ["twn"] = "TW",
        ["ukr"] = "UA", ["usa"] = "US", ["zaf"] = "ZA",
    };

    private static int? GetCommonSenseAge(JsonElement item)
    {
        if (!item.TryGetProperty("commonsense", out var cs))
        {
            return null;
        }

        var flagged = cs.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => cs.TryGetInt32(out var n) && n > 0,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(cs.GetString()) && cs.GetString() is not ("false" or "0"),
            _ => false,
        };
        if (!flagged)
        {
            return null;
        }

        if (GetInt(item, "age_rating") is { } age and > 0)
        {
            return age;
        }

        // Some responses carry the age in "commonsense" itself.
        return cs.ValueKind == JsonValueKind.Number && cs.TryGetInt32(out var n2) && n2 > 1 ? n2 : null;
    }

    private static string? GetTmdbId(JsonElement item)
    {
        if (item.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Object && GetIdString(ids, "tmdb") is { } nested)
        {
            return nested;
        }

        return GetIdString(item, "tmdbid") ?? GetIdString(item, "tmdb_id") ?? GetIdString(item, "id");
    }

    private static string? GetIdString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => value.GetString(),
            _ => null,
        };
    }

    private static int? GetInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.GetString()?.TrimEnd('+'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Wanted(IReadOnlyCollection<string> countries, string code) =>
        countries.Count == 0 || countries.Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));
}

/// <summary>What an OMDb lookup said.</summary>
public enum OmdbStatus
{
    /// <summary>An answer (possibly no rating).</summary>
    Ok,

    /// <summary>The daily request limit was reached.</summary>
    LimitReached,

    /// <summary>The API key was rejected.</summary>
    BadKey,
}

/// <summary>A parsed OMDb response.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Ratings">The rating, if any.</param>
public sealed record OmdbResult(OmdbStatus Status, List<RawRating> Ratings);
