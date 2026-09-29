using Jellyfin.Plugin.CommonSense.Rating;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CommonSense.Configuration;

/// <summary>What a user rule matches on.</summary>
public enum RuleMatch
{
    /// <summary>Items carrying a tag.</summary>
    Tag,

    /// <summary>Items with a genre.</summary>
    Genre,

    /// <summary>Items in a collection (box set), by name.</summary>
    Collection,
}

/// <summary>"Everything matching X gets rating Y" — applied before any source.</summary>
public class UserRule
{
    /// <summary>Gets or sets what to match on.</summary>
    public RuleMatch Match { get; set; } = RuleMatch.Tag;

    /// <summary>Gets or sets the tag, genre or collection name (case-insensitive).</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Gets or sets the rating to assign.</summary>
    public string Rating { get; set; } = "XXX";
}

/// <summary>Plugin settings, edited on the plugin's dashboard page.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the TMDb API key (v3 key or v4 read access token).</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether TMDb certifications are used.</summary>
    public bool UseTmdb { get; set; } = true;

    /// <summary>Gets or sets the countries whose TMDb certifications count, comma-separated ISO 3166-1 codes.</summary>
    public string Countries { get; set; } = "US, GB, IE, AU, NZ, DE";

    /// <summary>Gets or sets the MDBList API key.</summary>
    public string MdblistApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether Common Sense Media ages (via MDBList) are used.</summary>
    public bool UseCommonSense { get; set; } = true;

    /// <summary>Gets or sets how several sources' ratings become one.</summary>
    public CombineMode Combine { get; set; } = CombineMode.Median;

    /// <summary>Gets or sets a value indicating whether a score rounds to the nearest rating on the ladder (else up).</summary>
    public bool RoundToNearest { get; set; } = true;

    /// <summary>Gets or sets the user rules. Rules win over every source.</summary>
    public List<UserRule> Rules { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether ratings may only get stricter.</summary>
    public bool RaiseOnly { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether hand-set Custom Ratings are left alone.</summary>
    public bool RespectManualCustomRating { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether items whose rating field is locked are left alone.</summary>
    public bool RespectLockedRating { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether new items are rated automatically once their metadata arrives.</summary>
    public bool AutoRateNewItems { get; set; }

    /// <summary>Gets or sets the ratings a movie may be given, loosest first, comma-separated.</summary>
    public string MovieLadder { get; set; } = "G, PG, PG-13, R, NC-17";

    /// <summary>Gets or sets the ratings a series may be given, loosest first, comma-separated.</summary>
    public string SeriesLadder { get; set; } = "TV-Y, TV-Y7, TV-G, TV-PG, TV-14, TV-MA";

    /// <summary>Gets or sets replacements for legacy ratings Jellyfin can't score, one <c>OLD=NEW</c> per line.</summary>
    public string LegacyMap { get; set; } = "X=NC-17\nAO=XXX\nGP=PG\nM=PG\n18+=NC-17";

    /// <summary>Gets or sets ratings that Jellyfin scores but that mean nothing modern, comma-separated.</summary>
    public string UnverifiedRatings { get; set; } = "Approved, Passed";

    /// <summary>Gets or sets how long source lookups are cached, in days.</summary>
    public int CacheDays { get; set; } = 30;
}
