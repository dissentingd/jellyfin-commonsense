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
