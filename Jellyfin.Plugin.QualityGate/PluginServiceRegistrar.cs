using Jellyfin.Plugin.QualityGate.Api;
using Jellyfin.Plugin.QualityGate.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.QualityGate;

/// <summary>
/// Registers plugin services with the dependency injection container.
/// </summary>
public class PluginServiceRegistrar : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Register the custom intro provider for policy-based intro selection
        serviceCollection.AddSingleton<IIntroProvider, QualityGateIntroProvider>();
        
        // Register startup filter to inject MediaSource filtering middleware
        // This ensures restricted users don't see blocked versions in the UI
        serviceCollection.AddTransient<IStartupFilter, QualityGateStartupFilter>();
    }
}

