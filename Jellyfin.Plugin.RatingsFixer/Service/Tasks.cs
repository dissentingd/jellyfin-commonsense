using Jellyfin.Plugin.RatingsFixer.Ledger;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.RatingsFixer.Service;

/// <summary>Dry run: works out every change and saves a report, writes nothing.</summary>
/// <param name="service">The service.</param>
public sealed class PreviewTask(RatingsFixerService service) : IScheduledTask
{
    /// <summary>The task key.</summary>
    public const string TaskKey = "RatingsFixerPreview";

    /// <inheritdoc />
    public string Name => "Preview re-rating (dry run)";

    /// <inheritdoc />
    public string Key => TaskKey;

    /// <inheritdoc />
    public string Description => "Works out which items would get a stricter Custom Rating, and why. Writes nothing; see the report on the plugin's settings page.";

    /// <inheritdoc />
    public string Category => "Ratings Fixer";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        service.RunAsync(apply: false, onlyItems: null, saveReport: true, progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}

/// <summary>Writes the Custom Ratings a preview would propose, and records them in the ledger.</summary>
/// <param name="service">The service.</param>
public sealed class ApplyTask(RatingsFixerService service) : IScheduledTask
{
    /// <summary>The task key.</summary>
    public const string TaskKey = "RatingsFixerApply";

    /// <inheritdoc />
    public string Name => "Apply re-rating";

    /// <inheritdoc />
    public string Key => TaskKey;

    /// <inheritdoc />
    public string Description => "Writes stricter Custom Ratings and records each one in the ledger. Run the preview first.";

    /// <inheritdoc />
    public string Category => "Ratings Fixer";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        service.RunAsync(apply: true, onlyItems: null, saveReport: true, progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}

/// <summary>Re-applies the stored ledger — e.g. after restoring Jellyfin's database from a backup.</summary>
/// <param name="service">The service.</param>
public sealed class RestoreTask(RatingsFixerService service) : IScheduledTask
{
    /// <summary>The task key.</summary>
    public const string TaskKey = "RatingsFixerRestore";

    /// <inheritdoc />
    public string Name => "Restore ratings from ledger";

    /// <inheritdoc />
    public string Key => TaskKey;

    /// <inheritdoc />
    public string Description => "Re-applies every Custom Rating in the ledger, matching items by provider ids when their Jellyfin ids have changed.";

    /// <inheritdoc />
    public string Category => "Ratings Fixer";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        service.RestoreAsync([.. RatingLedger.Load(RatingsFixerService.LedgerPath).Entries], apply: true, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
