using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JfPrewarm;

/// <summary>
/// Registers the background prewarm worker and the HTTP request hook.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PrewarmService>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<PrewarmService>());
        serviceCollection.AddSingleton<IStartupFilter, PrewarmStartupFilter>();
    }
}
