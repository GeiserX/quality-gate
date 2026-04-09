using Jellyfin.Plugin.QualityGate.Filters;
using Jellyfin.Plugin.QualityGate.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.QualityGate;

/// <summary>
/// Registers plugin services with the dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Register the filter in DI explicitly
        serviceCollection.AddScoped<MediaSourceResultFilter>();

        // Use PostConfigure to ensure the filter is added after all other MVC configuration
        serviceCollection.PostConfigure<MvcOptions>(options =>
        {
            options.Filters.AddService<MediaSourceResultFilter>();
        });

        // Intro provider for policy-based intro selection
        serviceCollection.AddSingleton<IIntroProvider, QualityGateIntroProvider>();

        // Entry point for session monitoring
        serviceCollection.AddHostedService<QualityGateEntryPoint>();
    }
}
