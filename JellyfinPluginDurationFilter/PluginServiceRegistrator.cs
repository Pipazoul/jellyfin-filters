using JellyfinPluginDurationFilter.Injection;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace JellyfinPluginDurationFilter
{
    /// <summary>
    /// Registers the plugin's services with the Jellyfin host DI container.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            // Runs once at server startup: injects the client script into jellyfin-web.
            serviceCollection.AddHostedService<WebInjectionService>();
        }
    }
}
