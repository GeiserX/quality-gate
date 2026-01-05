using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.QualityGate;

/// <summary>
/// Registers plugin services with the DI container.
/// </summary>
public class PluginServiceRegistrar : IPluginServiceRegistrar
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<QualityGateService>();
    }
}

