using System.Collections.Concurrent;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RatingsFixer.Service;

/// <summary>
/// When enabled, rates movies and series as their metadata arrives, and gives new episodes their
/// series' rating, so new additions are covered without waiting for a scheduled run. Items are
/// batched for a minute so a big library scan becomes a few runs, not thousands.
/// </summary>
/// <param name="libraryManager">Library manager.</param>
/// <param name="service">The service.</param>
/// <param name="context">The plugin's settings.</param>
/// <param name="logger">Logger.</param>
public sealed class AutoRateService(ILibraryManager libraryManager, RatingsFixerService service, IPluginContext context, ILogger<AutoRateService> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan _delay = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();
    private readonly CancellationTokenSource _stopping = new();
    private Timer? _timer;
    private int _running;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemUpdated += OnItemUpdated;
        libraryManager.ItemAdded += OnItemAdded;
        _timer = new Timer(_ => _ = FlushAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemUpdated -= OnItemUpdated;
        libraryManager.ItemAdded -= OnItemAdded;
        _stopping.Cancel();
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _stopping.Dispose();
    }

    /// <summary>
    /// Which item a library event should queue for rating, if any: a movie or series whose metadata
    /// was just downloaded, or the series of a newly added season or episode (re-running the series
    /// pushes its rating down). Our own writes are <c>MetadataEdit</c>, so they never re-trigger this.
    /// </summary>
    /// <param name="item">The item the event is about.</param>
    /// <param name="added">True for <c>ItemAdded</c>, false for <c>ItemUpdated</c>.</param>
    /// <param name="reason">The update reason (ignored for additions).</param>
    /// <returns>The id to queue, or null.</returns>
    internal static Guid? QueueTarget(BaseItem item, bool added, ItemUpdateType reason)
    {
        if (item.IsVirtualItem)
        {
            return null;
        }

        if (added)
        {
            var seriesId = item switch
            {
                Episode episode => episode.SeriesId,
                Season season => season.SeriesId,
                _ => Guid.Empty,
            };
            return seriesId == Guid.Empty ? null : seriesId;
        }

        return item is Movie or Series && reason.HasFlag(ItemUpdateType.MetadataDownload) ? item.Id : null;
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e) => OnEvent(e.Item, added: false, e.UpdateReason);

    private void OnItemAdded(object? sender, ItemChangeEventArgs e) => OnEvent(e.Item, added: true, e.UpdateReason);

    private void OnEvent(BaseItem item, bool added, ItemUpdateType reason)
    {
        if (context.Configuration.AutoRateNewItems && QueueTarget(item, added, reason) is { } id)
        {
            Queue(id);
        }
    }

    private void Queue(Guid id)
    {
        _pending.TryAdd(id, 0);
        _timer?.Change(_delay, Timeout.InfiniteTimeSpan);
    }

    private async Task FlushAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            _timer?.Change(_delay, Timeout.InfiniteTimeSpan);
            return;
        }

        try
        {
            var ids = _pending.Keys.ToList();
            foreach (var id in ids)
            {
                _pending.TryRemove(id, out _);
            }

            if (ids.Count > 0)
            {
                var report = await service.RunAsync(apply: true, ids, saveReport: false, progress: null, _stopping.Token).ConfigureAwait(false);
                var written = report.Entries.Count(e => e.Applied);
                if (written > 0)
                {
                    logger.LogInformation("Ratings Fixer auto-rated {Written} of {Count} updated item(s)", written, ids.Count);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ratings Fixer auto-rating failed");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
