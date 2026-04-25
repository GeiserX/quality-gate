using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.QualityGate.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Moq;

namespace Jellyfin.Plugin.QualityGate.Tests;

public class PluginTests : IDisposable
{
    private readonly Plugin _plugin;
    private readonly string _tempDir;

    public PluginTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qg-plugin-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.SetReturnsDefault<string>(_tempDir);
        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(new PluginConfiguration());
        _plugin = new Plugin(appPaths.Object, xmlSerializer.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Plugin_Name_IsQualityGate()
    {
        Assert.Equal("QualityGate", _plugin.Name);
    }

    [Fact]
    public void Plugin_Id_IsExpectedGuid()
    {
        Assert.Equal(Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"), _plugin.Id);
    }

    [Fact]
    public void Plugin_Description_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(_plugin.Description));
    }

    [Fact]
    public void Plugin_Instance_IsSet()
    {
        Assert.NotNull(Plugin.Instance);
        Assert.Same(_plugin, Plugin.Instance);
    }

    [Fact]
    public void Plugin_Configuration_IsNotNull()
    {
        Assert.NotNull(_plugin.Configuration);
    }

    [Fact]
    public void Plugin_GetPages_ReturnsTwoPages()
    {
        var pages = _plugin.GetPages().ToList();
        Assert.Equal(2, pages.Count);
    }

    [Fact]
    public void Plugin_GetPages_HasConfigPageHtml()
    {
        var pages = _plugin.GetPages().ToList();
        var configPage = pages.First(p => p.Name == "QualityGate");
        Assert.NotNull(configPage);
        Assert.True(configPage.EnableInMainMenu);
        Assert.Contains("configPage.html", configPage.EmbeddedResourcePath);
    }

    [Fact]
    public void Plugin_GetPages_HasConfigPageJs()
    {
        var pages = _plugin.GetPages().ToList();
        var jsPage = pages.First(p => p.Name == "configPage.js");
        Assert.NotNull(jsPage);
        Assert.Contains("configPage.js", jsPage.EmbeddedResourcePath);
    }
}
