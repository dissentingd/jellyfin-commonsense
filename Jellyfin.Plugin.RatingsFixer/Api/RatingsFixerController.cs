using System.Net.Mime;
using System.Text;
using Jellyfin.Plugin.RatingsFixer.Configuration;
using Jellyfin.Plugin.RatingsFixer.Ledger;
using Jellyfin.Plugin.RatingsFixer.Rating;
using Jellyfin.Plugin.RatingsFixer.Service;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.RatingsFixer.Api;

/// <summary>Admin endpoints behind the settings page.</summary>
/// <param name="service">The service.</param>
/// <param name="scale">The rating scale.</param>
/// <param name="configurationManager">Server configuration.</param>
[ApiController]
[Route("RatingsFixer")]
[Authorize(Policy = "RequiresElevation")]
public class RatingsFixerController(RatingsFixerService service, IRatingScale scale, IServerConfigurationManager configurationManager) : ControllerBase
{
    /// <summary>Gets the latest preview/apply report.</summary>
    /// <returns>The report.</returns>
    [HttpGet("Report")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<RunReport> GetReport() =>
        service.LoadReport() is { } report ? report : NotFound();

    /// <summary>Downloads the ledger.</summary>
    /// <returns>The ledger JSON.</returns>
    [HttpGet("Ledger")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult GetLedger() => JsonFile(
        RatingLedger.Serialize(RatingLedger.Load(service.LedgerPath).Entries),
        "ratings-fixer-ledger.json");

    /// <summary>Downloads every Custom Rating in the library (including hand-set ones) in ledger format.</summary>
    /// <returns>The export JSON.</returns>
    [HttpGet("Export")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult Export() => JsonFile(
        RatingLedger.Serialize(service.ExportCustomRatings()),
        "ratings-fixer-export.json");

    /// <summary>Re-applies a ledger: the uploaded one, or the stored one when the body is empty.</summary>
    /// <param name="apply">Whether to write; false just reports what would change.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The restore report.</returns>
    [HttpPost("Restore")]
    public async Task<ActionResult<RestoreReport>> Restore([FromQuery] bool apply, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        List<LedgerEntry> entries;
        try
        {
            entries = string.IsNullOrWhiteSpace(body)
                ? [.. RatingLedger.Load(service.LedgerPath).Entries]
                : RatingLedger.Parse(body);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return BadRequest($"That isn't a ledger file: {ex.Message}");
        }

        return await service.RestoreAsync(entries, apply, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets every rating the plugin has written (the ledger), for browsing and reverting.</summary>
    /// <returns>The entries, newest first.</returns>
    [HttpGet("LedgerEntries")]
    public ActionResult<IEnumerable<LedgerEntry>> LedgerEntries() =>
        RatingLedger.Load(service.LedgerPath).Entries.OrderByDescending(e => e.Timestamp).ToList();

    /// <summary>Reverts ratings the plugin wrote.</summary>
    /// <param name="itemIds">Item ids from the ledger.</param>
    /// <param name="apply">Whether to write; false just reports what would happen.</param>
    /// <param name="exclude">Whether to exclude the reverted titles from future runs.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The revert report.</returns>
    [HttpPost("Revert")]
    public async Task<ActionResult<RevertReport>> Revert([FromBody] Guid[] itemIds, [FromQuery] bool apply, [FromQuery] bool exclude, CancellationToken cancellationToken) =>
        await service.RevertAsync(itemIds, apply, exclude, cancellationToken).ConfigureAwait(false);

    /// <summary>Gets the titles excluded from re-rating.</summary>
    /// <returns>The exclusions.</returns>
    [HttpGet("Exclusions")]
    public ActionResult<IEnumerable<ExclusionEntry>> Exclusions() => service.GetExclusions();

    /// <summary>Lets excluded titles be re-rated again.</summary>
    /// <param name="itemIds">Item ids.</param>
    /// <returns>How many were included again.</returns>
    [HttpPost("Exclusions/Remove")]
    public ActionResult<int> IncludeAgain([FromBody] Guid[] itemIds) => service.IncludeAgain(itemIds);

    /// <summary>Re-checks just the latest report's review list, asking the sources afresh.</summary>
    /// <param name="apply">Whether to write changes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The re-check's report, or 404 when there's no report yet.</returns>
    [HttpPost("RecheckReview")]
    public async Task<ActionResult<RunReport>> RecheckReview([FromQuery] bool apply, CancellationToken cancellationToken) =>
        await service.RecheckReviewAsync(apply, cancellationToken).ConfigureAwait(false) is { } report ? report : NotFound();

    /// <summary>
    /// Gets the recommended values for the tuning settings (policy, countries, suggestions). They're the plugin's
    /// defaults, which were tuned on a large real library; keys, rules and library choices aren't included.
    /// </summary>
    /// <returns>Setting name → recommended value.</returns>
    [HttpGet("Recommended")]
    public ActionResult<Dictionary<string, object>> Recommended()
    {
        var d = new PluginConfiguration();
        return new Dictionary<string, object>
        {
            [nameof(d.Combine)] = d.Combine.ToString(),
            [nameof(d.RoundToNearest)] = d.RoundToNearest,
            [nameof(d.RaiseOnly)] = d.RaiseOnly,
            [nameof(d.RespectManualCustomRating)] = d.RespectManualCustomRating,
            [nameof(d.RespectLockedRating)] = d.RespectLockedRating,
            [nameof(d.Countries)] = d.Countries,
            [nameof(d.FallbackCountries)] = d.FallbackCountries,
            [nameof(d.UseCommonSense)] = d.UseCommonSense,
            [nameof(d.LegacyMap)] = d.LegacyMap,
            [nameof(d.UnverifiedRatings)] = d.UnverifiedRatings,
            [nameof(d.CacheDays)] = d.CacheDays,
            [nameof(d.MatureKeywords)] = d.MatureKeywords,
            [nameof(d.MatureRating)] = d.MatureRating,
            [nameof(d.GenreRatings)] = d.GenreRatings,
            [nameof(d.EraTags)] = d.EraTags,
            [nameof(d.SuggestFromCollections)] = d.SuggestFromCollections,
            [nameof(d.SuggestionCollectionMaxSize)] = d.SuggestionCollectionMaxSize,
        };
    }

    /// <summary>Gets the review list with metadata and suggested ratings.</summary>
    /// <returns>The review items.</returns>
    [HttpGet("Review")]
    public ActionResult<IEnumerable<ReviewItem>> Review() => service.GetReviewItems();

    /// <summary>Writes ratings chosen in the review panel.</summary>
    /// <param name="assignments">The choices.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was written.</returns>
    [HttpPost("Review/Assign")]
    public async Task<ActionResult<AssignReport>> Assign([FromBody] ReviewAssignment[] assignments, CancellationToken cancellationToken) =>
        await service.AssignAsync(assignments, cancellationToken).ConfigureAwait(false);

    /// <summary>Deletes cached source lookups, so the next run asks the sources again.</summary>
    /// <returns>No content.</returns>
    [HttpPost("ClearCache")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearCache()
    {
        var dir = service.DataDir;
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.GetFiles(dir, "cache-*.json"))
            {
                System.IO.File.Delete(file);
            }
        }

        return NoContent();
    }

    /// <summary>Scores a rating the way the server does — handy for checking a mapping.</summary>
    /// <param name="rating">The rating, e.g. <c>15</c>.</param>
    /// <param name="country">Optional ISO country, e.g. <c>GB</c>.</param>
    /// <returns>The score, or 404 when the server doesn't know the rating.</returns>
    [HttpGet("Score")]
    public ActionResult<object> Score([FromQuery] string rating, [FromQuery] string? country) =>
        scale.Score(rating, string.IsNullOrWhiteSpace(country) ? null : country) is { } s
            ? new { rating, country, score = s.Score, subScore = s.SubScore }
            : NotFound();

    /// <summary>Gets the ready-made rating ladders, and the server's metadata country to match them against.</summary>
    /// <returns>The presets and the server's country.</returns>
    [HttpGet("Presets")]
    public ActionResult<object> Presets() => new
    {
        serverCountry = configurationManager.Configuration.MetadataCountryCode,
        presets = LadderPresets.All,
    };

    private FileContentResult JsonFile(string json, string name) =>
        File(Encoding.UTF8.GetBytes(json), MediaTypeNames.Application.Json, name);
}
