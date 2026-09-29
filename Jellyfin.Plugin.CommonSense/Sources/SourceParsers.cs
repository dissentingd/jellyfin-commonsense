using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.CommonSense.Rating;

namespace Jellyfin.Plugin.CommonSense.Sources;

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
