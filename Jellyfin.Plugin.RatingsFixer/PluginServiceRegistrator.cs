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
        serviceCollection.AddSingleton<IPluginContext, PluginContext>();
        serviceCollection.AddSingleton<IRatingScale, JellyfinRatingScale>();
        serviceCollection.AddSingleton<TmdbSource>();
        serviceCollection.AddSingleton<IRatingSource>(sp => sp.GetRequiredService<TmdbSource>());
        serviceCollection.AddSingleton<ITmdbIdResolver>(sp => sp.GetRequiredService<TmdbSource>());
        serviceCollection.AddSingleton<IRatingSource, MdblistSource>();
        serviceCollection.AddSingleton<IRatingSource, TvdbSource>();
        serviceCollection.AddSingleton<RatingsFixerService>();
        serviceCollection.AddHostedService<AutoRateService>();
    }
}
