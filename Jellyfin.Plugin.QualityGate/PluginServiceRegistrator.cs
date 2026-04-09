using Jellyfin.Plugin.QualityGate.Filters;
using Jellyfin.Plugin.QualityGate.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.QualityGate;

/// <summary>
/// Registers plugin services with the dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // MVC result filter for MediaSource filtering
        // Operates on C# objects before serialization, unaffected by response compression
        serviceCollection.Configure<MvcOptions>(options =>
        {
            options.Filters.Add<MediaSourceResultFilter>();
        });

        // Intro provider for policy-based intro selection
        serviceCollection.AddSingleton<IIntroProvider, QualityGateIntroProvider>();

        // Entry point for session monitoring
        serviceCollection.AddHostedService<QualityGateEntryPoint>();
    }
}
