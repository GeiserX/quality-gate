using Jellyfin.Plugin.QualityGate.Middleware;
using Jellyfin.Plugin.QualityGate.Startup;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.QualityGate;

/// <summary>
/// Registers plugin services with the DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Register the entry point for session monitoring
        serviceCollection.AddHostedService<QualityGateEntryPoint>();
        
        // Register the startup filter to add middleware
        serviceCollection.AddTransient<IStartupFilter, QualityGateStartupFilter>();
        
        // Register middleware for DI
        serviceCollection.AddTransient<QualityGateMiddleware>();
    }
}
