using System;
using System.IO;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.QualityGate.Configuration;
using MediaBrowser.Common.Configuration;
using Moq;

namespace Jellyfin.Plugin.QualityGate.Tests.Harness;

/// <summary>
/// Saves and reloads the plugin configuration through the real plugin and a real XML file, the
/// way a server does across a restart.
///
/// Each <see cref="Start"/> builds a fresh <see cref="Plugin"/> over the same configuration
/// folder, so reading its <c>Configuration</c> is a genuine load from disk, not a cached object.
/// Only the application paths are faked, to point that folder at a temporary directory.
///
/// Constructing a <see cref="Plugin"/> replaces the static <c>Plugin.Instance</c>, so a test
/// class using this belongs in <see cref="PluginInstanceCollection"/>.
/// </summary>
public sealed class ConfigRoundTrip : IDisposable
{
    private readonly string _dir;
    private readonly IApplicationPaths _appPaths;

    /// <summary>Initializes a new instance of the <see cref="ConfigRoundTrip"/> class over an empty folder.</summary>
    public ConfigRoundTrip()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qg-xml-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.SetReturnsDefault<string>(_dir);
        _appPaths = appPaths.Object;
    }

    /// <summary>Gets the path of the configuration file the plugin reads and writes.</summary>
    public string ConfigPath => Start().ConfigurationFilePath;

    /// <summary>Starts the plugin as a server would, over whatever is on disk.</summary>
    /// <returns>A new plugin instance that has not yet loaded its configuration.</returns>
    public Plugin Start() => new(_appPaths, new RealXmlSerializer());

    /// <summary>
    /// Saves <paramref name="config"/> through the plugin to XML, then restarts and loads it back.
    /// This is the XML half of a page save only: the object never passes through JSON. Use
    /// <see cref="SaveFromPageJson"/> for the whole path.
    /// </summary>
    /// <param name="config">The configuration to save.</param>
    /// <returns>The configuration a restarted server sees.</returns>
    public PluginConfiguration SaveAndReload(PluginConfiguration config)
    {
        Start().UpdateConfiguration(config);
        return Load();
    }

    /// <summary>
    /// Saves the JSON the page posts as the page's Save button does: Jellyfin deserialises the body
    /// with its own JSON defaults (the Guid converter included), then the plugin writes XML. Then
    /// restarts and loads it back.
    /// </summary>
    /// <param name="json">The body the page posts.</param>
    /// <returns>The configuration a restarted server sees.</returns>
    public PluginConfiguration SaveFromPageJson(string json)
        => SaveAndReload(JsonSerializer.Deserialize<PluginConfiguration>(json, JsonDefaults.Options)
            ?? throw new InvalidDataException("the page JSON deserialised to null"));

    /// <summary>Restarts and loads whatever is on disk.</summary>
    /// <returns>The configuration a restarted server sees.</returns>
    public PluginConfiguration Load() => Start().Configuration;

    /// <summary>Writes a configuration file by hand, for example one shaped like an earlier release wrote it.</summary>
    /// <param name="xml">The file contents.</param>
    public void WriteXml(string xml) => File.WriteAllText(ConfigPath, xml);

    /// <summary>Reads the configuration file as it sits on disk.</summary>
    /// <returns>The file contents.</returns>
    public string ReadXml() => File.ReadAllText(ConfigPath);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }
}
