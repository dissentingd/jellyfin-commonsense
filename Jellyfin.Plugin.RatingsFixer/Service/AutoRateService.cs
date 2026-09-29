using System.Collections.Concurrent;
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
/// <param name="logger">Logger.</param>
public sealed class AutoRateService(ILibraryManager libraryManager, RatingsFixerService service, ILogger<AutoRateService> logger) : IHostedService, IDisposable
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

    private static bool Enabled => Plugin.Instance?.Configuration.AutoRateNewItems == true;

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        // Our own writes are MetadataEdit, so they never re-trigger this.
        if (Enabled
            && e.Item is Movie or Series
            && !e.Item.IsVirtualItem
            && e.UpdateReason.HasFlag(ItemUpdateType.MetadataDownload))
        {
            Queue(e.Item.Id);
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        // A new episode of a series that already has our rating: re-running the series pushes it down.
        if (Enabled && !e.Item.IsVirtualItem)
        {
            var seriesId = e.Item switch
            {
                Episode episode => episode.SeriesId,
                Season season => season.SeriesId,
                _ => Guid.Empty,
            };
            if (seriesId != Guid.Empty)
            {
                Queue(seriesId);
            }
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
