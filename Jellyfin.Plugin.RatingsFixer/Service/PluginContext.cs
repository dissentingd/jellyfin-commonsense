using Jellyfin.Plugin.RatingsFixer.Configuration;

namespace Jellyfin.Plugin.RatingsFixer.Service;

/// <summary>The plugin's settings and data folder, injected so services can be tested without a running plugin.</summary>
public interface IPluginContext
{
    /// <summary>Gets the current configuration.</summary>
    PluginConfiguration Configuration { get; }

    /// <summary>Gets the folder for the ledger, report and caches.</summary>
    string DataFolderPath { get; }
}

/// <summary>The running plugin's context.</summary>
public sealed class PluginContext : IPluginContext
{
    /// <inheritdoc />
    public PluginConfiguration Configuration => Plugin.Instance!.Configuration;

    /// <inheritdoc />
    public string DataFolderPath => Plugin.Instance!.DataFolderPath;
}
