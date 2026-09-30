using System.Text.Json;

namespace Jellyfin.Plugin.RatingsFixer.Ledger;

/// <summary>A title the plugin must leave alone (set when a rating is reverted with "don't re-rate").</summary>
public sealed class ExclusionEntry
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the production year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is a series.</summary>
    public bool IsSeries { get; set; }

    /// <summary>Gets or sets provider ids, so the exclusion survives re-scans and database restores.</summary>
    public Dictionary<string, string> ProviderIds
    {
        get;
        set => field = new Dictionary<string, string>(value ?? [], StringComparer.OrdinalIgnoreCase);
    }

    = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the rating that was reverted.</summary>
    public string? RevertedRating { get; set; }

    /// <summary>Gets or sets when (UTC).</summary>
    public DateTime Added { get; set; }
}

/// <summary>Titles excluded from re-rating. One JSON file in the plugin's data folder.</summary>
public sealed class ExclusionList
{
    private readonly string _path;
    private readonly Dictionary<Guid, ExclusionEntry> _entries;

    private ExclusionList(string path, IEnumerable<ExclusionEntry> entries)
    {
        _path = path;
        _entries = entries.GroupBy(e => e.ItemId).ToDictionary(g => g.Key, g => g.Last());
    }

    /// <summary>Gets all entries.</summary>
    public IReadOnlyCollection<ExclusionEntry> Entries => _entries.Values;

    /// <summary>Loads the list, or starts an empty one.</summary>
    /// <param name="path">File path.</param>
    /// <returns>The list.</returns>
    public static ExclusionList Load(string path) =>
        new(path, File.Exists(path) ? JsonSerializer.Deserialize<List<ExclusionEntry>>(File.ReadAllText(path), RatingLedger.JsonOptions) ?? [] : []);

    /// <summary>Whether an item is excluded, matching by item id, then by any shared provider id of the same kind.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="isSeries">Whether the item is a series.</param>
    /// <param name="providerIds">The item's provider ids.</param>
    /// <returns>Whether it's excluded.</returns>
    public bool Contains(Guid itemId, bool isSeries, IReadOnlyDictionary<string, string> providerIds)
    {
        if (_entries.ContainsKey(itemId))
        {
            return true;
        }

        return _entries.Values.Any(e => e.IsSeries == isSeries
            && providerIds.Any(p => !string.IsNullOrWhiteSpace(p.Value) && e.ProviderIds.TryGetValue(p.Key, out var v) && string.Equals(v, p.Value, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Adds or replaces an entry.</summary>
    /// <param name="entry">The entry.</param>
    public void Add(ExclusionEntry entry) => _entries[entry.ItemId] = entry;

    /// <summary>Removes an entry.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>Whether there was one.</returns>
    public bool Remove(Guid itemId) => _entries.Remove(itemId);

    /// <summary>Writes the list to disk.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(), RatingLedger.JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }
}
