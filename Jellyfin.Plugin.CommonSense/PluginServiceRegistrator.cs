using Jellyfin.Plugin.CommonSense.Rating;
using Jellyfin.Plugin.CommonSense.Service;
using Jellyfin.Plugin.CommonSense.Sources;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.CommonSense;

/// <summary>Registers the plugin's services.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(Plugin.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd($"jellyfin-commonsense/{typeof(Plugin).Assembly.GetName().Version}");
        });
        serviceCollection.AddSingleton<IRatingScale, JellyfinRatingScale>();
        serviceCollection.AddSingleton<IRatingSource, TmdbSource>();
        serviceCollection.AddSingleton<IRatingSource, MdblistSource>();
        serviceCollection.AddSingleton<CommonSenseService>();
        serviceCollection.AddHostedService<AutoRateService>();
    }
}
