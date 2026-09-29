using System.Text.Json;
using Jellyfin.Plugin.RatingsFixer.Rating;

namespace Jellyfin.Plugin.RatingsFixer.Sources;

/// <summary>
/// On-disk cache of source lookups, so a preview followed by an apply (or a nightly run)
/// doesn't re-query every title — MDBList's free tier has a daily request limit.
/// Empty results are cached too: "TMDb has nothing for this" is an answer.
/// </summary>
public sealed class SourceCache
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };
    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries;
    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new();
    private bool _dirty;

    private SourceCache(string path, Dictionary<string, Entry> entries)
    {
        _path = path;
        _entries = entries;
    }

    /// <summary>Loads a cache file, or starts an empty one if it's missing or unreadable.</summary>
    /// <param name="path">File path.</param>
    /// <returns>The cache.</returns>
    public static SourceCache Load(string path)
    {
        Dictionary<string, Entry>? entries = null;
        try
        {
            if (File.Exists(path))
            {
                entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), _json);
            }
        }
        catch (JsonException)
        {
            // A corrupt cache only costs a re-fetch.
        }

        return new SourceCache(path, entries ?? new Dictionary<string, Entry>(StringComparer.Ordinal));
    }

    /// <summary>Gets a fresh-enough entry.</summary>
    /// <param name="key">Cache key.</param>
    /// <param name="maxAge">Maximum age.</param>
    /// <param name="ratings">The cached ratings.</param>
    /// <returns>Whether a fresh entry was found.</returns>
    public bool TryGet(string key, TimeSpan maxAge, out List<RawRating> ratings)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.Fetched <= maxAge)
            {
                ratings = entry.Ratings;
                return true;
            }
        }

        ratings = [];
        return false;
    }

    /// <summary>Stores an entry.</summary>
    /// <param name="key">Cache key.</param>
    /// <param name="ratings">Ratings to cache (may be empty).</param>
    public void Set(string key, List<RawRating> ratings)
    {
        lock (_lock)
        {
            _entries[key] = new Entry(DateTime.UtcNow, ratings);
            _dirty = true;
        }
    }

    /// <summary>Drops every entry.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _dirty = true;
        }
    }

    /// <summary>Writes the cache to disk if it changed.</summary>
    public void Save()
    {
        lock (_saveLock)
        {
            string text;
            lock (_lock)
            {
                if (!_dirty)
                {
                    return;
                }

                text = JsonSerializer.Serialize(_entries, _json);
                _dirty = false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, _path, overwrite: true);
        }
    }

    /// <summary>A cached lookup.</summary>
    /// <param name="Fetched">When it was fetched (UTC).</param>
    /// <param name="Ratings">What the source returned.</param>
    public sealed record Entry(DateTime Fetched, List<RawRating> Ratings);
}
