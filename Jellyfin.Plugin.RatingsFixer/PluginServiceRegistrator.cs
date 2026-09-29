using Jellyfin.Plugin.RatingsFixer.Rating;
using Jellyfin.Plugin.RatingsFixer.Service;
using Jellyfin.Plugin.RatingsFixer.Sources;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.RatingsFixer;

/// <summary>Registers the plugin's services.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(Plugin.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd($"jellyfin-ratings-fixer/{typeof(Plugin).Assembly.GetName().Version}");
        });
        serviceCollection.AddSingleton<IRatingScale, JellyfinRatingScale>();
        serviceCollection.AddSingleton<IRatingSource, TmdbSource>();
        serviceCollection.AddSingleton<IRatingSource, MdblistSource>();
        serviceCollection.AddSingleton<RatingsFixerService>();
        serviceCollection.AddHostedService<AutoRateService>();
    }
}
