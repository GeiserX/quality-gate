using Jellyfin.Plugin.QualityGate.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
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
        
        // NOTE: MediaSource filtering middleware disabled due to response encoding issues.
        // The middleware approach conflicts with Jellyfin's response compression.
        // For now, restricted users may see all versions but playback is only allowed for 720p.
        // Future: implement proper response filtering or use separate libraries approach.
    }
}

