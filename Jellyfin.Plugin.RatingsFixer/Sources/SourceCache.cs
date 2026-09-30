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

/// <summary>
/// On-disk map from an external id (e.g. an IMDb id) to a TMDb id. Found ids never expire;
/// "not found" answers are retried after <c>retryAfter</c>.
/// </summary>
public sealed class IdMapCache
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };
    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries;
    private readonly Lock _lock = new();
    private bool _dirty;

    private IdMapCache(string path, Dictionary<string, Entry> entries)
    {
        _path = path;
        _entries = entries;
    }

    /// <summary>Loads the map, or starts an empty one.</summary>
    /// <param name="path">File path.</param>
    /// <returns>The map.</returns>
    public static IdMapCache Load(string path)
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
            // A corrupt map only costs a re-lookup.
        }

        return new IdMapCache(path, entries ?? new Dictionary<string, Entry>(StringComparer.Ordinal));
    }

    /// <summary>Looks up a cached answer.</summary>
    /// <param name="key">Cache key.</param>
    /// <param name="retryAfter">How long a "not found" answer stays valid.</param>
    /// <param name="id">The TMDb id, or null when it was not found.</param>
    /// <returns>Whether a usable answer is cached.</returns>
    public bool TryGet(string key, TimeSpan retryAfter, out string? id)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry) && (entry.Id is not null || DateTime.UtcNow - entry.Fetched <= retryAfter))
            {
                id = entry.Id;
                return true;
            }
        }

        id = null;
        return false;
    }

    /// <summary>Stores an answer.</summary>
    /// <param name="key">Cache key.</param>
    /// <param name="id">The TMDb id, or null for "not found".</param>
    public void Set(string key, string? id)
    {
        lock (_lock)
        {
            _entries[key] = new Entry(DateTime.UtcNow, id);
            _dirty = true;
        }
    }

    /// <summary>Writes the map to disk if it changed.</summary>
    public void Save()
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

    /// <summary>A cached answer.</summary>
    /// <param name="Fetched">When it was looked up (UTC).</param>
    /// <param name="Id">The TMDb id, or null.</param>
    public sealed record Entry(DateTime Fetched, string? Id);
}
