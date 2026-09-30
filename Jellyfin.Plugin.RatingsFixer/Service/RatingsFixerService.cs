using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.RatingsFixer.Configuration;
using Jellyfin.Plugin.RatingsFixer.Ledger;
using Jellyfin.Plugin.RatingsFixer.Rating;
using Jellyfin.Plugin.RatingsFixer.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RatingsFixer.Service;

/// <summary>Adapts Jellyfin's rating tables to <see cref="IRatingScale"/>.</summary>
/// <param name="localization">Jellyfin's localization manager.</param>
public sealed class JellyfinRatingScale(ILocalizationManager localization) : IRatingScale
{
    /// <inheritdoc />
    public RatingScore? Score(string rating, string? country = null)
    {
        var score = localization.GetRatingScore(rating, country);
        return score is null ? null : new RatingScore(score.Score, score.SubScore);
    }
}

/// <summary>Runs previews, applies and restores against the library. One run at a time.</summary>
/// <param name="libraryManager">Library manager.</param>
/// <param name="scale">Rating scale.</param>
/// <param name="sources">Rating sources.</param>
/// <param name="context">The plugin's settings and data folder.</param>
/// <param name="logger">Logger.</param>
/// <param name="resolver">Finds TMDb ids from IMDb/TVDb ids; optional.</param>
public sealed class RatingsFixerService(
    ILibraryManager libraryManager,
    IRatingScale scale,
    IEnumerable<IRatingSource> sources,
    IPluginContext context,
    ILogger<RatingsFixerService> logger,
    ITmdbIdResolver? resolver = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Gets the folder holding the ledger, report and caches.</summary>
    public string DataDir => context.DataFolderPath;

    /// <summary>Gets the path of the last run report.</summary>
    public string ReportPath => Path.Combine(DataDir, "report.json");

    /// <summary>Gets the path of the ledger.</summary>
    public string LedgerPath => Path.Combine(DataDir, "ledger.json");

    /// <summary>Gets the path of the list of titles excluded from re-rating.</summary>
    public string ExclusionsPath => Path.Combine(DataDir, "exclusions.json");

    private PluginConfiguration Config => context.Configuration;

    /// <summary>Splits a comma- or newline-separated setting.</summary>
    /// <param name="value">The setting.</param>
    /// <returns>Trimmed, non-empty parts.</returns>
    public static List<string> SplitList(string? value) =>
        (value ?? string.Empty).Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Parses <c>OLD=NEW</c> lines.</summary>
    /// <param name="value">The setting.</param>
    /// <returns>The map.</returns>
    public static Dictionary<string, string> ParseMap(string? value)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (value ?? string.Empty).Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && eq < line.Length - 1)
            {
                map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        return map;
    }

    /// <summary>Builds engine options from the configuration.</summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The options.</returns>
    public static EngineOptions ToEngineOptions(PluginConfiguration config) => new()
    {
        RaiseOnly = config.RaiseOnly,
        Combine = config.Combine,
        RoundToNearest = config.RoundToNearest,
        RespectManualCustomRating = config.RespectManualCustomRating,
        RespectLockedRating = config.RespectLockedRating,
        MovieLadder = SplitList(config.MovieLadder),
        SeriesLadder = SplitList(config.SeriesLadder),
        LegacyMap = ParseMap(config.LegacyMap),
        UnverifiedRatings = SplitList(config.UnverifiedRatings),
    };

    /// <summary>Loads the last saved report.</summary>
    /// <returns>The report, or null.</returns>
    public RunReport? LoadReport() =>
        File.Exists(ReportPath) ? JsonSerializer.Deserialize<RunReport>(File.ReadAllText(ReportPath), RatingLedger.JsonOptions) : null;

    /// <summary>Previews or applies re-rating.</summary>
    /// <param name="apply">Whether to write changes.</param>
    /// <param name="onlyItems">Restrict to these items (auto-rating); null for the whole library.</param>
    /// <param name="saveReport">Whether to save this run as the latest report.</param>
    /// <param name="progress">Progress, 0–100.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="refreshSources">Ask the sources again instead of using cached answers.</param>
    /// <returns>The report.</returns>
    public async Task<RunReport> RunAsync(bool apply, IReadOnlyCollection<Guid>? onlyItems, bool saveReport, IProgress<double>? progress, CancellationToken cancellationToken, bool refreshSources = false)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(apply, onlyItems, saveReport, progress, cancellationToken, refreshSources).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Re-applies ledger entries, matching items by id, then by provider ids.</summary>
    /// <param name="entries">The entries to restore.</param>
    /// <param name="apply">Whether to write changes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report.</returns>
    public async Task<RestoreReport> RestoreAsync(IReadOnlyList<LedgerEntry> entries, bool apply, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var report = new RestoreReport { Applied = apply, Entries = entries.Count };
            var index = new ItemIndex(LoadItems(null, allLibraries: true));

            var ledger = RatingLedger.Load(LedgerPath);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = index.Find(entry.ItemId, entry.IsSeries, entry.ProviderIds);
                if (item is null)
                {
                    report.Unmatched.Add($"{entry.Name} ({entry.Year})");
                    continue;
                }

                var previous = item.CustomRating;
                try
                {
                    if (string.Equals(item.CustomRating, entry.CustomRating, StringComparison.Ordinal))
                    {
                        report.AlreadyCorrect++;
                    }
                    else
                    {
                        report.Changed++;
                        if (apply)
                        {
                            await WriteAsync(item, entry.CustomRating, cancellationToken).ConfigureAwait(false);
                            ledger.Upsert(ToLedgerEntry(item, previous, entry.CustomRating, "restored from ledger"));
                        }
                    }

                    if (item is Series)
                    {
                        report.ChildrenUpdated += await CascadeAsync(item, entry.CustomRating, previous, apply, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    report.Errors.Add($"{entry.Name}: {ex.Message}");
                }
            }

            if (apply)
            {
                ledger.Save();
            }

            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Snapshots every Custom Rating in the library (ours or not) as ledger entries.</summary>
    /// <returns>The entries.</returns>
    public List<LedgerEntry> ExportCustomRatings() =>
        LoadItems(null, allLibraries: true)
            .Where(i => !string.IsNullOrWhiteSpace(i.CustomRating))
            .Select(i => ToLedgerEntry(i, null, i.CustomRating!, "export"))
            .ToList();

    /// <summary>
    /// Undoes ratings the plugin wrote: each item goes back to the custom rating it had before the plugin
    /// first touched it (usually none), and a series' seasons and episodes that carry its rating go back
    /// too. Items whose rating was changed by someone else since are left alone.
    /// </summary>
    /// <param name="itemIds">Ledger item ids to revert.</param>
    /// <param name="apply">Whether to write; false just reports what would happen.</param>
    /// <param name="exclude">Whether to exclude the reverted titles from future runs.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report.</returns>
    public async Task<RevertReport> RevertAsync(IReadOnlyCollection<Guid> itemIds, bool apply, bool exclude, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var report = new RevertReport { Applied = apply, Requested = itemIds.Count };
            var ledger = RatingLedger.Load(LedgerPath);
            var exclusions = ExclusionList.Load(ExclusionsPath);
            var index = new ItemIndex(LoadItems(null, allLibraries: true));
            foreach (var id in itemIds.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ledger.Find(id) is not { } entry)
                {
                    report.NotInLedger++;
                    continue;
                }

                var label = entry.Year is null ? entry.Name : $"{entry.Name} ({entry.Year})";
                var item = index.Find(entry.ItemId, entry.IsSeries, entry.ProviderIds);
                if (item is null)
                {
                    report.NotFound.Add(label);
                    continue;
                }

                if (!string.Equals(item.CustomRating, entry.CustomRating, StringComparison.Ordinal))
                {
                    report.ChangedSince.Add($"{label}: now \"{item.CustomRating}\", not \"{entry.CustomRating}\"");
                    continue;
                }

                var previous = string.IsNullOrWhiteSpace(entry.PreviousCustomRating) ? null : entry.PreviousCustomRating;
                report.Reverted++;
                if (item is Series)
                {
                    report.ChildrenReverted += await RevertChildrenAsync(item, entry.CustomRating, previous, apply, cancellationToken).ConfigureAwait(false);
                }

                if (!apply)
                {
                    continue;
                }

                try
                {
                    await WriteAsync(item, previous, cancellationToken).ConfigureAwait(false);
                    ledger.Remove(entry.ItemId);
                    if (exclude)
                    {
                        exclusions.Add(new ExclusionEntry
                        {
                            ItemId = item.Id,
                            Name = item.Name ?? entry.Name,
                            Year = item.ProductionYear,
                            IsSeries = item is Series,
                            ProviderIds = new Dictionary<string, string>(item.ProviderIds.Where(p => !string.IsNullOrWhiteSpace(p.Value)), StringComparer.OrdinalIgnoreCase),
                            RevertedRating = entry.CustomRating,
                            Added = DateTime.UtcNow,
                        });
                        report.Excluded++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    report.Reverted--;
                    report.Errors.Add($"{label}: {ex.Message}");
                }
            }

            if (apply)
            {
                ledger.Save();
                exclusions.Save();
            }

            logger.LogInformation(
                "Ratings Fixer revert{Mode}: {Reverted} of {Requested} reverted, {Children} seasons/episodes, {Excluded} excluded",
                apply ? string.Empty : " (preview)",
                report.Reverted,
                report.Requested,
                report.ChildrenReverted,
                report.Excluded);
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-checks only the titles on the latest report's review list, asking the sources afresh (bypassing the
    /// cache), and folds the result back into the saved report.
    /// </summary>
    /// <param name="apply">Whether to write changes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The re-check's own report, or null when there's no saved report.</returns>
    public async Task<RunReport?> RecheckReviewAsync(bool apply, CancellationToken cancellationToken)
    {
        var saved = LoadReport();
        if (saved is null)
        {
            return null;
        }

        var ids = saved.Entries.Where(e => e.Kind == DecisionKind.Review).Select(e => e.ItemId).ToHashSet();
        if (ids.Count == 0)
        {
            return new RunReport { Applied = apply, Started = DateTime.UtcNow, Finished = DateTime.UtcNow };
        }

        var recheck = await RunAsync(apply, ids, saveReport: false, progress: null, cancellationToken, refreshSources: true).ConfigureAwait(false);

        // Fold into the saved report: the old review entries are replaced by whatever the re-check decided.
        saved.Entries = [.. saved.Entries.Where(e => !ids.Contains(e.ItemId)), .. recheck.Entries];
        foreach (var kind in Enum.GetValues<DecisionKind>().Select(k => k.ToString()))
        {
            var before = saved.Counts.GetValueOrDefault(kind) - (kind == nameof(DecisionKind.Review) ? ids.Count : 0);
            saved.Counts[kind] = Math.Max(0, before) + recheck.Counts.GetValueOrDefault(kind);
        }

        saved.Applied |= apply;
        saved.Finished = recheck.Finished;
        saved.ChildrenUpdated += recheck.ChildrenUpdated;
        saved.Warnings = [.. saved.Warnings, .. recheck.Warnings.Select(w => "Review re-check: " + w)];
        Directory.CreateDirectory(DataDir);
        await File.WriteAllTextAsync(ReportPath, JsonSerializer.Serialize(saved, RatingLedger.JsonOptions), cancellationToken).ConfigureAwait(false);
        return recheck;
    }

    /// <summary>Gets the titles excluded from re-rating.</summary>
    /// <returns>The entries.</returns>
    public List<ExclusionEntry> GetExclusions() =>
        [.. ExclusionList.Load(ExclusionsPath).Entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Lets excluded titles be re-rated again by the next run.</summary>
    /// <param name="itemIds">Item ids.</param>
    /// <returns>How many were removed from the exclusion list.</returns>
    public int IncludeAgain(IReadOnlyCollection<Guid> itemIds)
    {
        var exclusions = ExclusionList.Load(ExclusionsPath);
        var removed = itemIds.Count(exclusions.Remove);
        exclusions.Save();
        return removed;
    }

    private async Task<RunReport> RunCoreAsync(bool apply, IReadOnlyCollection<Guid>? onlyItems, bool saveReport, IProgress<double>? progress, CancellationToken ct, bool refreshSources = false)
    {
        var config = Config;
        var report = new RunReport { Applied = apply, Started = DateTime.UtcNow };
        var engine = new RatingEngine(scale, ToEngineOptions(config));
        foreach (var bad in engine.UnscoredLadderEntries)
        {
            report.Warnings.Add($"Ladder rating \"{bad}\" isn't known to this server's rating table and was ignored.");
        }

        var ledger = RatingLedger.Load(LedgerPath);
        var exclusions = ExclusionList.Load(ExclusionsPath);
        var items = LoadItems(onlyItems);
        report.ItemsScanned = items.Count;
        var snapshots = items.ToDictionary(i => i.Id, i => Snapshot(i, ledger, exclusions));
        progress?.Report(2);

        // 1. User rules.
        var ruleHits = MatchRules(config, items, engine, report.Warnings);

        // 2. Sources, for items no rule decided and that we might change.
        var needSources = snapshots.Values.Where(s => !ruleHits.ContainsKey(s.Id) && !WillSkip(s, config)).ToList();
        var raw = await FetchSourcesAsync(config, needSources, report.Warnings, progress, ct, refreshSources).ConfigureAwait(false);
        progress?.Report(80);

        // 3. Decide, and write.
        var countries = SplitList(config.Countries);
        var counts = Enum.GetValues<DecisionKind>().ToDictionary(k => k.ToString(), _ => 0);
        var done = 0;
        var itemsSinceSave = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var snap = snapshots[item.Id];
            var ratings = raw.GetValueOrDefault(item.Id, [])
                .Where(r => r.Kind == CandidateKind.CommonSense
                    ? config.UseCommonSense
                    : r.Source != "MDBList" // MDBList "certification" has no reliable country; older caches may hold it
                        && r.Country is not null
                        && (countries.Count == 0 || countries.Contains(r.Country, StringComparer.OrdinalIgnoreCase)))
                .ToList();
            var decision = engine.Decide(snap, ruleHits.GetValueOrDefault(item.Id, []), ratings);
            counts[decision.Kind.ToString()]++;
            var changes = decision.Kind is DecisionKind.Raise or DecisionKind.Lower;
            var previousCustom = item.CustomRating;
            var entry = decision.Kind == DecisionKind.Keep ? null : ToReportEntry(decision);

            if (apply && changes)
            {
                try
                {
                    await WriteAsync(item, decision.NewRating!, ct).ConfigureAwait(false);
                    ledger.Upsert(ToLedgerEntry(item, previousCustom, decision.NewRating!, decision.Reason));
                    entry!.Applied = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    entry!.Error = ex.Message;
                    logger.LogWarning(ex, "Failed to write Custom Rating for {Name}", item.Name);
                }
            }

            // A series' rating only hides its episodes from search, Next Up and Latest if the
            // episodes carry it too, so push it down - including to episodes added since last time.
            if (!changes && snap.CustomRatingIsOurs && EnforcementStale(item))
            {
                // Our rating is recorded but Jellyfin isn't enforcing it (written before the fix): re-save it.
                report.Repaired++;
                if (apply)
                {
                    try
                    {
                        await WriteAsync(item, item.CustomRating!, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Failed to repair Custom Rating for {Name}", item.Name);
                    }
                }
            }

            if (apply && ++itemsSinceSave >= 200)
            {
                ledger.Save(); // so an interrupted run still knows which ratings are ours
                itemsSinceSave = 0;
            }

            var seriesRating = changes ? decision.NewRating : snap.CustomRatingIsOurs ? item.CustomRating : null;
            if (item is Series && seriesRating is not null && entry?.Error is null)
            {
                var children = await CascadeAsync(item, seriesRating, previousCustom, apply, ct).ConfigureAwait(false);
                report.ChildrenUpdated += children;
                if (entry is not null)
                {
                    entry.ChildrenUpdated = children;
                }
            }

            if (entry is not null)
            {
                report.Entries.Add(entry);
            }

            progress?.Report(80 + (20.0 * ++done / Math.Max(1, items.Count)));
        }

        if (apply)
        {
            ledger.Save();
        }

        report.Counts = counts;
        report.Finished = DateTime.UtcNow;
        if (saveReport)
        {
            Directory.CreateDirectory(DataDir);
            await File.WriteAllTextAsync(ReportPath, JsonSerializer.Serialize(report, RatingLedger.JsonOptions), ct).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Ratings Fixer {Mode}: {Scanned} items — {Counts}",
            apply ? "apply" : "preview",
            report.ItemsScanned,
            string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}")));
        foreach (var warning in report.Warnings)
        {
            logger.LogWarning("Ratings Fixer: {Warning}", warning);
        }

        return report;
    }

    private static bool WillSkip(ItemSnapshot s, PluginConfiguration config) =>
        s.Excluded
        || (config.RespectLockedRating && s.RatingLocked)
        || (config.RespectManualCustomRating && !string.IsNullOrWhiteSpace(s.CustomRating) && !s.CustomRatingIsOurs);

    private static ItemSnapshot Snapshot(BaseItem item, RatingLedger ledger, ExclusionList exclusions)
    {
        var ours = ledger.Find(item.Id);
        return new ItemSnapshot
        {
            Id = item.Id,
            Name = item.Name ?? string.Empty,
            Year = item.ProductionYear,
            IsSeries = item is Series,
            OfficialRating = item.OfficialRating,
            CustomRating = item.CustomRating,
            ProviderIds = new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase),
            RatingLocked = item.LockedFields.Contains(MetadataField.OfficialRating),
            CustomRatingIsOurs = ours is not null && string.Equals(ours.CustomRating, item.CustomRating, StringComparison.Ordinal),
            Excluded = exclusions.Contains(item.Id, item is Series, item.ProviderIds),
        };
    }

    private static LedgerEntry ToLedgerEntry(BaseItem item, string? previousCustom, string newCustom, string reason) => new()
    {
        ItemId = item.Id,
        Name = item.Name ?? string.Empty,
        Year = item.ProductionYear,
        IsSeries = item is Series,
        ProviderIds = new Dictionary<string, string>(item.ProviderIds.Where(p => !string.IsNullOrWhiteSpace(p.Value)), StringComparer.OrdinalIgnoreCase),
        PreviousOfficialRating = item.OfficialRating,
        PreviousCustomRating = previousCustom,
        CustomRating = newCustom,
        Reason = reason,
        Timestamp = DateTime.UtcNow,
    };

    private static ReportEntry ToReportEntry(Decision d) => new()
    {
        ItemId = d.Item.Id,
        Name = d.Item.Name,
        Year = d.Item.Year,
        IsSeries = d.Item.IsSeries,
        Kind = d.Kind,
        Current = d.CurrentRating,
        New = d.NewRating,
        Reason = d.Reason,
        Candidates = d.Candidates.Select(c => $"{c.Origin} = {c.Score}").ToList(),
    };

    private List<BaseItem> LoadItems(IReadOnlyCollection<Guid>? onlyItems, bool allLibraries = false)
    {
        InternalItemsQuery Query(Guid? library)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                Recursive = true,
                IsVirtualItem = false,
            };
            if (library is { } id)
            {
                // A recursive ParentId query covers everything inside that library.
                query.ParentId = id;
            }

            if (onlyItems is not null)
            {
                query.ItemIds = [.. onlyItems];
            }

            return query;
        }

        var libraries = allLibraries ? [] : SelectedLibraries();
        var found = libraries.Count == 0
            ? libraryManager.GetItemList(Query(null))
            : libraries.SelectMany(l => libraryManager.GetItemList(Query(l))).DistinctBy(i => i.Id).ToList();
        return found.Where(i => i is Movie or Series).ToList();
    }

    private List<Guid> SelectedLibraries() =>
        (Config.LibraryIds ?? [])
            .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();

    private Dictionary<Guid, List<RatingCandidate>> MatchRules(PluginConfiguration config, List<BaseItem> items, RatingEngine engine, List<string> warnings)
    {
        var hits = new Dictionary<Guid, List<RatingCandidate>>();
        foreach (var rule in config.Rules.Where(r => !string.IsNullOrWhiteSpace(r.Value)))
        {
            var origin = $"rule: {rule.Match.ToString().ToLowerInvariant()} \"{rule.Value}\" → {rule.Rating}";
            var candidate = engine.RuleCandidate(origin, rule.Rating);
            if (candidate is null)
            {
                warnings.Add($"Rule {origin}: \"{rule.Rating}\" isn't a rating this server can score, so the rule was skipped.");
                continue;
            }

            IEnumerable<BaseItem> matched = rule.Match switch
            {
                RuleMatch.Tag => items.Where(i => i.Tags.Contains(rule.Value, StringComparer.OrdinalIgnoreCase)),
                RuleMatch.Genre => items.Where(i => i.Genres.Contains(rule.Value, StringComparer.OrdinalIgnoreCase)),
                RuleMatch.Collection => CollectionMembers(rule.Value, items, warnings),
                _ => [],
            };

            foreach (var item in matched)
            {
                if (!hits.TryGetValue(item.Id, out var list))
                {
                    hits[item.Id] = list = [];
                }

                list.Add(candidate);
            }
        }

        return hits;
    }

    private IEnumerable<BaseItem> CollectionMembers(string name, List<BaseItem> items, List<string> warnings)
    {
        var boxSets = libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.BoxSet], Recursive = true })
            .OfType<Folder>()
            .Where(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (boxSets.Count == 0)
        {
            warnings.Add($"No collection named \"{name}\" was found.");
            return [];
        }

        var memberIds = boxSets.SelectMany(b => b.GetLinkedChildren()).Select(c => c.Id).ToHashSet();
        return items.Where(i => memberIds.Contains(i.Id));
    }

    private async Task<Dictionary<Guid, List<RawRating>>> FetchSourcesAsync(PluginConfiguration config, List<ItemSnapshot> items, List<string> warnings, IProgress<double>? progress, CancellationToken ct, bool refresh = false)
    {
        var merged = new Dictionary<Guid, List<RawRating>>();
        var tmdbKey = config.TmdbApiKey.Trim();
        var enabled = sources.Where(s => s.Name switch
        {
            "TMDb" => config.UseTmdb && tmdbKey.Length > 0,
            "MDBList" => !string.IsNullOrWhiteSpace(config.MdblistApiKey),
            "TVDb" => config.UseTvdb && !string.IsNullOrWhiteSpace(config.TvdbApiKey),
            _ => false,
        }).ToList();
        if (enabled.Count == 0)
        {
            warnings.Add("No source is configured (TMDb, MDBList or TVDb API key), so only rules and legacy mappings were applied.");
            return merged;
        }

        // A refresh treats every cached answer (and every cached "not found" id lookup) as stale.
        var maxAge = refresh ? TimeSpan.Zero : TimeSpan.FromDays(Math.Max(0, config.CacheDays));
        items = await ResolveTmdbIdsAsync(items, tmdbKey, maxAge, warnings, ct).ConfigureAwait(false);
        var withoutTmdb = items.Count(i => i.TmdbId is null);
        if (withoutTmdb > 0)
        {
            warnings.Add($"{withoutTmdb} item(s) have no TMDb id (and none could be found from their IMDb/TVDb ids), so TMDb and MDBList couldn't be asked about them.");
        }

        for (var s = 0; s < enabled.Count; s++)
        {
            var source = enabled[s];
            var cache = SourceCache.Load(Path.Combine(DataDir, $"cache-{source.Name.ToLowerInvariant()}.json"));
            var (key, pin) = source.Name switch
            {
                "TMDb" => (tmdbKey, (string?)null),
                "TVDb" => (config.TvdbApiKey.Trim(), config.TvdbPin.Trim()),
                _ => (config.MdblistApiKey.Trim(), null),
            };
            var slice = new Progress<double>(p => progress?.Report(2 + (78.0 * (s + (p / 100)) / enabled.Count)));
            try
            {
                var found = await source.FetchAsync(items, new SourceRun(key, cache, maxAge, warnings, pin), slice, ct).ConfigureAwait(false);
                foreach (var (id, ratings) in found)
                {
                    if (!merged.TryGetValue(id, out var list))
                    {
                        merged[id] = list = [];
                    }

                    list.AddRange(ratings);
                }
            }
            catch (SourceUnavailableException ex)
            {
                warnings.Add(ex.Message);
            }
            catch (HttpRequestException ex)
            {
                warnings.Add($"{source.Name} couldn't be reached: {ex.Message}");
            }
            finally
            {
                cache.Save();
            }
        }

        return merged;
    }

    /// <summary>
    /// For items with no TMDb id, looks one up from their IMDb (or, for series, TVDb) id, so every source
    /// keyed by TMDb id can be asked about them. Jellyfin's own provider ids are left untouched.
    /// </summary>
    private async Task<List<ItemSnapshot>> ResolveTmdbIdsAsync(List<ItemSnapshot> items, string tmdbKey, TimeSpan retryAfter, List<string> warnings, CancellationToken ct)
    {
        var missing = items.Where(i => i.TmdbId is null && (i.GetProviderId("Imdb") is not null || (i.IsSeries && i.GetProviderId("Tvdb") is not null))).ToList();
        if (resolver is null || tmdbKey.Length == 0 || missing.Count == 0)
        {
            return items;
        }

        var cache = IdMapCache.Load(Path.Combine(DataDir, "tmdb-ids.json"));
        try
        {
            var found = await resolver.ResolveAsync(missing, tmdbKey, cache, retryAfter, ct).ConfigureAwait(false);
            return items.Select(i => found.TryGetValue(i.Id, out var tmdbId)
                ? i with { ProviderIds = new Dictionary<string, string>(i.ProviderIds, StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = tmdbId } }
                : i).ToList();
        }
        catch (SourceUnavailableException ex)
        {
            warnings.Add(ex.Message);
            return items;
        }
        catch (HttpRequestException ex)
        {
            warnings.Add($"TMDb couldn't be reached to look up missing ids: {ex.Message}");
            return items;
        }
        finally
        {
            cache.Save();
        }
    }

    /// <summary>
    /// Gives a series' seasons and episodes its Custom Rating, the way Jellyfin's own metadata
    /// editor does - except it only ever makes a child stricter, and never touches official ratings.
    /// </summary>
    /// <returns>How many children were (or, in a preview, would be) updated.</returns>
    private async Task<int> CascadeAsync(BaseItem series, string rating, string? previousSeriesCustom, bool apply, CancellationToken ct)
    {
        if (scale.Score(rating) is not { } target)
        {
            return 0;
        }

        var respectManual = Config.RespectManualCustomRating;
        var children = libraryManager.GetItemList(new InternalItemsQuery
        {
            AncestorIds = [series.Id],
            IncludeItemTypes = [BaseItemKind.Season, BaseItemKind.Episode],
            Recursive = true,
        });

        var toWrite = new List<BaseItem>();
        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            var effective = new[] { child.CustomRating, child.OfficialRating }.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))?.Trim();
            if (effective is not null && scale.Score(effective) is { } current && current >= target)
            {
                // Already rated enough - but a write from before the enforcement fix may not be in effect.
                if (string.Equals(child.CustomRating, rating, StringComparison.Ordinal) && EnforcementStale(child))
                {
                    toWrite.Add(child);
                }

                continue;
            }

            // A looser custom rating on an episode is someone's deliberate choice, unless it's the
            // series rating that we (or the metadata editor's own cascade) put there before.
            if (respectManual
                && !string.IsNullOrWhiteSpace(child.CustomRating)
                && !string.Equals(child.CustomRating, previousSeriesCustom, StringComparison.Ordinal))
            {
                continue;
            }

            toWrite.Add(child);
        }

        if (apply)
        {
            // One repository save per chunk instead of one per episode: a long-running series is
            // hundreds of items.
            foreach (var chunk in toWrite.Chunk(100))
            {
                foreach (var child in chunk)
                {
                    SetRating(child, rating);
                }

                await libraryManager.UpdateItemsAsync(chunk, series, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            }
        }

        var updated = toWrite.Count;
        return updated;
    }

    /// <summary>
    /// Puts back a series' seasons and episodes that carry the rating being reverted. The cascade only ever
    /// wrote children that had no custom rating or the series' previous one, so both go back to that.
    /// </summary>
    /// <returns>How many children were (or, in a preview, would be) reverted.</returns>
    private async Task<int> RevertChildrenAsync(BaseItem series, string rating, string? previous, bool apply, CancellationToken ct)
    {
        var children = libraryManager.GetItemList(new InternalItemsQuery
        {
            AncestorIds = [series.Id],
            IncludeItemTypes = [BaseItemKind.Season, BaseItemKind.Episode],
            Recursive = true,
        }).Where(c => string.Equals(c.CustomRating, rating, StringComparison.Ordinal)).ToList();

        if (apply)
        {
            foreach (var chunk in children.Chunk(100))
            {
                foreach (var child in chunk)
                {
                    SetRating(child, previous);
                }

                await libraryManager.UpdateItemsAsync(chunk, series, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            }
        }

        return children.Count;
    }

    /// <summary>
    /// Whether the score Jellyfin actually filters on (<c>InheritedParentalRatingValue</c>) is out of
    /// step with the item's rating. The metadata editor recomputes it on save; a plain
    /// <c>UpdateItemAsync</c> doesn't, so a Custom Rating written without it isn't enforced at all.
    /// </summary>
    private static bool EnforcementStale(BaseItem item)
    {
        var score = item.GetParentalRatingScore();
        return item.InheritedParentalRatingValue != score?.Score
            || (item.InheritedParentalRatingSubValue ?? 0) != (score?.SubScore ?? 0);
    }

    private static void SetRating(BaseItem item, string? rating)
    {
        item.CustomRating = rating;
        var score = item.GetParentalRatingScore();
        item.InheritedParentalRatingValue = score?.Score;
        item.InheritedParentalRatingSubValue = score?.SubScore;
    }

    private async Task WriteAsync(BaseItem item, string? rating, CancellationToken ct)
    {
        SetRating(item, rating);
        await libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
    }

    /// <summary>Finds library items by id, then by provider id (so ledger entries survive re-scans and restores).</summary>
    private sealed class ItemIndex
    {
        private readonly Dictionary<Guid, BaseItem> _byId;
        private readonly Dictionary<string, BaseItem> _byProvider = new(StringComparer.OrdinalIgnoreCase);

        public ItemIndex(IReadOnlyCollection<BaseItem> items)
        {
            _byId = items.ToDictionary(i => i.Id);
            foreach (var item in items)
            {
                foreach (var (provider, id) in item.ProviderIds)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        _byProvider.TryAdd(Key(item is Series, provider, id), item);
                    }
                }
            }
        }

        public BaseItem? Find(Guid itemId, bool isSeries, IReadOnlyDictionary<string, string> providerIds) =>
            _byId.GetValueOrDefault(itemId)
            ?? providerIds.Select(p => _byProvider.GetValueOrDefault(Key(isSeries, p.Key, p.Value))).FirstOrDefault(i => i is not null);

        private static string Key(bool isSeries, string provider, string id) => $"{(isSeries ? "s" : "m")}:{provider}:{id}";
    }
}
