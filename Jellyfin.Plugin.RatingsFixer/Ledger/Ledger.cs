using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.RatingsFixer.Ledger;

/// <summary>One Custom Rating this plugin wrote (or, in an export, one that exists).</summary>
public sealed class LedgerEntry
{
    /// <summary>Gets or sets the Jellyfin item id at the time of writing.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the production year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is a series.</summary>
    public bool IsSeries { get; set; }

    /// <summary>Gets or sets provider ids — how entries are matched back after re-scans or a DB restore.</summary>
    public Dictionary<string, string> ProviderIds
    {
        get;
        set => field = new Dictionary<string, string>(value ?? [], StringComparer.OrdinalIgnoreCase);
    }

    = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the official rating when the entry was written.</summary>
    public string? PreviousOfficialRating { get; set; }

    /// <summary>Gets or sets the custom rating that was replaced, if any.</summary>
    public string? PreviousCustomRating { get; set; }

    /// <summary>Gets or sets the custom rating written.</summary>
    public string CustomRating { get; set; } = string.Empty;

    /// <summary>Gets or sets why.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets when (UTC).</summary>
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// The record of every Custom Rating written. Kept as one JSON file in the plugin's
/// data folder; downloadable from the settings page, and re-appliable with Restore.
/// </summary>
public sealed class RatingLedger
{
    /// <summary>JSON options shared by the ledger and its exports.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Dictionary<Guid, LedgerEntry> _entries;

    private RatingLedger(string path, IEnumerable<LedgerEntry> entries)
    {
        _path = path;
        _entries = entries.GroupBy(e => e.ItemId).ToDictionary(g => g.Key, g => g.Last());
    }

    /// <summary>Gets all entries.</summary>
    public IReadOnlyCollection<LedgerEntry> Entries => _entries.Values;

    /// <summary>Loads the ledger, or starts an empty one.</summary>
    /// <param name="path">File path.</param>
    /// <returns>The ledger.</returns>
    public static RatingLedger Load(string path) =>
        new(path, File.Exists(path) ? Parse(File.ReadAllText(path)) : []);

    /// <summary>Parses ledger JSON (an array of entries).</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The entries.</returns>
    public static List<LedgerEntry> Parse(string json) =>
        JsonSerializer.Deserialize<List<LedgerEntry>>(json, JsonOptions) ?? [];

    /// <summary>Serializes entries as ledger JSON.</summary>
    /// <param name="entries">The entries.</param>
    /// <returns>The JSON.</returns>
    public static string Serialize(IEnumerable<LedgerEntry> entries) =>
        JsonSerializer.Serialize(entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Year).ToList(), JsonOptions);

    /// <summary>Gets the entry for an item.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>The entry, or null.</returns>
    public LedgerEntry? Find(Guid itemId) => _entries.GetValueOrDefault(itemId);

    /// <summary>
    /// Adds or replaces an item's entry. When the item's previous custom rating is one we wrote ourselves,
    /// the original "previous" values are carried over, so a revert goes back to what was there before the
    /// plugin ever touched the item — not to its own earlier write.
    /// </summary>
    /// <param name="entry">The entry.</param>
    public void Upsert(LedgerEntry entry)
    {
        if (_entries.TryGetValue(entry.ItemId, out var existing)
            && string.Equals(existing.CustomRating, entry.PreviousCustomRating, StringComparison.Ordinal))
        {
            entry.PreviousCustomRating = existing.PreviousCustomRating;
            entry.PreviousOfficialRating = existing.PreviousOfficialRating;
        }

        _entries[entry.ItemId] = entry;
    }

    /// <summary>Removes an item's entry.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>Whether there was one.</returns>
    public bool Remove(Guid itemId) => _entries.Remove(itemId);

    /// <summary>Writes the ledger to disk.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, Serialize(_entries.Values));
        File.Move(tmp, _path, overwrite: true);
    }
}
