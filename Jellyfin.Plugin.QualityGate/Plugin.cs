using System;
using System.Collections.Generic;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.EncodePriority;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.QualityGate;

/// <summary>
/// Quality Gate plugin for Jellyfin.
/// Restricts users to specific media versions based on policies.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "QualityGate";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("9cab70ca-0af3-4d3a-adab-6a0df2496a33");

    /// <inheritdoc />
    public override string Description => "Cap the resolution a user may be served, measured against the media's actual height, and assign per-policy intro videos.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Removes every encode priority list the plugin wrote, so an encoder is not left reordering its
    /// queue by a file nothing will ever update again. Best effort: it needs only the state file
    /// and file access, and a failure must not block the uninstall.
    /// </summary>
    public override void OnUninstalling()
    {
        try
        {
            var statePath = EncodePriorityPaths.StateFile(ApplicationPaths.DataPath);
            var state = EncodePriorityState.Load(statePath, out _);
            CleanupPass.Run(state, new HashSet<string>());
            state.Save(statePath);
        }
        catch (Exception)
        {
            // Nothing sensible to do while the plugin is being removed.
        }

        base.OnUninstalling();
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                EnableInMainMenu = true
            },
            new PluginPageInfo
            {
                Name = "configPage.js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.js"
            }
        };
    }
}
