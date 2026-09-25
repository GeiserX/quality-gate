using Jellyfin.Plugin.QualityGate.EncodePriority;
using Jellyfin.Plugin.QualityGate.Filters;
using Jellyfin.Plugin.QualityGate.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
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
        // Resolution enforcement. Registered with PostConfigure, never plain Configure:
        // a plugin's RegisterServices runs from ApplicationHost.Init, which the host builder
        // invokes before the web host runs Startup.ConfigureServices and its AddJellyfinApi.
        // A Configure<MvcOptions> delegate registered here is therefore queued ahead of
        // Jellyfin's own MVC setup; PostConfigure runs after every IConfigureOptions<MvcOptions>
        // has been applied, so the filter is still on the collection once MVC is built.
        serviceCollection.AddScoped<ResolutionCapFilter>();
        serviceCollection.PostConfigure<MvcOptions>(options =>
        {
            options.Filters.AddService<ResolutionCapFilter>();
        });

        // MediaSourceResultFilter stays in the assembly but is intentionally NOT registered.
        // Its filename-pattern rewriting of item and listing responses has not been
        // re-validated against the Jellyfin 12 ABI, and the resolution cap does not need it.
        serviceCollection.AddSingleton<IIntroProvider, QualityGateIntroProvider>();

        // Encode priority triggers: playback start and settings saved. Does in-memory checks only
        // while the feature is off.
        serviceCollection.AddHostedService<EncodePriorityEvents>();
    }
}
