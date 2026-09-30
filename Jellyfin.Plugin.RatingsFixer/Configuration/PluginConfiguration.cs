using Jellyfin.Plugin.RatingsFixer.Rating;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RatingsFixer.Configuration;

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

    /// <summary>
    /// Gets or sets countries whose certifications count only when none of <see cref="Countries"/> (nor Common
    /// Sense) rates a title, comma-separated. They fill gaps without changing any other decision.
    /// </summary>
    public string FallbackCountries { get; set; } = "CA, FR, NL, BE, SE, NO, DK, FI, ES, PT, IT, BR, MX, JP, KR, SG, PL, RO";

    /// <summary>Gets or sets the MDBList API key.</summary>
    public string MdblistApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether Common Sense Media ages (via MDBList) are used.</summary>
    public bool UseCommonSense { get; set; } = true;

    /// <summary>Gets or sets the TVDb v4 API key (used for series).</summary>
    public string TvdbApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the TVDb subscriber PIN, needed with user-supported keys.</summary>
    public string TvdbPin { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether TVDb content ratings are used for series.</summary>
    public bool UseTvdb { get; set; } = true;

    /// <summary>Gets or sets the OMDb API key.</summary>
    public string OmdbApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether OMDb (IMDb's US rating) fills gaps no other source covers.</summary>
    public bool UseOmdb { get; set; } = true;

    /// <summary>Gets or sets the libraries to process (library item ids); empty means all.</summary>
    public string[] LibraryIds { get; set; } = [];

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

    /// <summary>Gets or sets keyword fragments that suggest mature content on the review list, comma-separated.</summary>
    public string MatureKeywords { get; set; } = "nudity, sex scene, erotic, gore, splatter, slasher, torture, rape, incest, prostitut, stripper, porn, drug addiction, drug abuse, heroin, serial killer, cannibal, graphic violence";

    /// <summary>Gets or sets the rating mature keywords suggest.</summary>
    public string MatureRating { get; set; } = "R";

    /// <summary>Gets or sets genre → rating suggestions for the review list, one <c>Genre=Rating</c> per line.</summary>
    public string GenreRatings { get; set; } = "Horror=R\nThriller=PG-13\nCrime=PG-13\nWar=PG-13\nDocumentary=PG-13\nAnimation=PG\nFamily=PG";

    /// <summary>Gets or sets a value indicating whether ratings of other titles in the same collection inform suggestions.</summary>
    public bool SuggestFromCollections { get; set; } = true;

    /// <summary>
    /// Gets or sets the largest collection whose titles inform suggestions. Franchises are small; big collections
    /// are usually lists ("Top Rated", a whole label's catalogue) whose strictest member says nothing about a title.
    /// </summary>
    public int SuggestionCollectionMaxSize { get; set; } = 30;

    /// <summary>Gets or sets tag → rating era hints for the review list, one <c>tag=Rating</c> per line.</summary>
    public string EraTags { get; set; } = "pre-code=PG-13";

    /// <summary>Gets or sets how long source lookups are cached, in days.</summary>
    public int CacheDays { get; set; } = 30;
}
