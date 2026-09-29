using Jellyfin.Plugin.CommonSense.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.CommonSense;

/// <summary>Re-rates a library to modern parental-rating standards via Custom Rating.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Name of the HTTP client used for source lookups.</summary>
    public const string HttpClientName = "CommonSense";

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the running instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Common Sense Ratings";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("65127be7-01ef-469f-b13b-ecfa1289f5ed");

    /// <inheritdoc />
    public override string Description => "Re-rates movies and series to modern parental-rating standards, so parental controls work on old and unrated titles.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
        },
    ];
}
