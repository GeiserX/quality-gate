using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Serialization;
using Moq;

namespace Jellyfin.Plugin.QualityGate.Tests;

public class FilterMediaSourcesTests : IDisposable
{
    private readonly Plugin _plugin;
    private readonly string _tempDir;

    public FilterMediaSourcesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qg-test-" + Guid.NewGuid().ToString("N")[..8]);
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

    private string CreateTempFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "test");
        return path;
    }

    private void SetConfig(PluginConfiguration config)
    {
        _plugin.Configuration.Policies = config.Policies;
        _plugin.Configuration.UserPolicies = config.UserPolicies;
        _plugin.Configuration.DefaultPolicyId = config.DefaultPolicyId;
    }

    private static QualityPolicy MakePolicy(
        string id = "p1",
        List<string>? allowedPatterns = null,
        List<string>? blockedPatterns = null)
    {
        return new QualityPolicy
        {
            Id = id,
            Name = "Test Policy",
            Enabled = true,
            AllowedFilenamePatterns = allowedPatterns ?? new List<string>(),
            BlockedFilenamePatterns = blockedPatterns ?? new List<string>(),
        };
    }

    private static MediaSourceInfo MakeSource(string path)
    {
        return new MediaSourceInfo { Path = path };
    }

    [Fact]
    public void NoPolicy_ReturnsAllSources()
    {
        var userId = Guid.NewGuid();
        SetConfig(new PluginConfiguration());

        var sources = new[]
        {
            MakeSource("/media/Movie - 1080p.mkv"),
            MakeSource("/media/Movie - 720p.mkv"),
        };

        var result = QualityGateService.FilterMediaSources(userId, sources).ToList();
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void WithPolicy_FiltersBlockedSources()
    {
        var userId = Guid.NewGuid();
        var path720 = CreateTempFile("Movie - 720p.mkv");
        var path1080 = CreateTempFile("Movie - 1080p.mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }),
            },
            DefaultPolicyId = "p1",
        });

        var sources = new[] { MakeSource(path720), MakeSource(path1080) };
        var result = QualityGateService.FilterMediaSources(userId, sources).ToList();

        Assert.Single(result);
        Assert.Equal(path720, result[0].Path);
    }

    [Fact]
    public void DenyAllPolicy_FiltersEverything()
    {
        var userId = Guid.NewGuid();
        var path = CreateTempFile("Movie.mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>(),
            DefaultPolicyId = "nonexistent",
        });

        var sources = new[] { MakeSource(path) };
        var result = QualityGateService.FilterMediaSources(userId, sources).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void FullAccessUser_BypassesDefaultPolicy()
    {
        var userId = Guid.NewGuid();
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }),
            },
            DefaultPolicyId = "p1",
            UserPolicies = new List<UserPolicyAssignment>
            {
                new() { UserId = userId, PolicyId = UserPolicyAssignment.FullAccessPolicyId },
            },
        });

        var sources = new[]
        {
            MakeSource("/media/Movie - 1080p.mkv"),
            MakeSource("/media/Movie - 720p.mkv"),
        };

        var result = QualityGateService.FilterMediaSources(userId, sources).ToList();
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void CanAccessPath_AllowedByPolicy()
    {
        var userId = Guid.NewGuid();
        var path = CreateTempFile("Movie - 720p.mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }),
            },
            DefaultPolicyId = "p1",
        });

        Assert.True(QualityGateService.CanAccessPath(userId, path));
    }

    [Fact]
    public void CanAccessPath_DeniedByPolicy()
    {
        var userId = Guid.NewGuid();
        var path = CreateTempFile("Movie - 2160p.mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }),
            },
            DefaultPolicyId = "p1",
        });

        Assert.False(QualityGateService.CanAccessPath(userId, path));
    }

    [Fact]
    public void CanAccessPath_NullPath_Denied()
    {
        var userId = Guid.NewGuid();
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy() },
            DefaultPolicyId = "p1",
        });

        Assert.False(QualityGateService.CanAccessPath(userId, null));
    }

    [Fact]
    public void NonexistentFile_DeniedEvenIfPatternMatches()
    {
        var userId = Guid.NewGuid();
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }),
            },
            DefaultPolicyId = "p1",
        });

        Assert.False(QualityGateService.CanAccessPath(userId, "/nonexistent/Movie - 720p.mkv"));
    }

    [Fact]
    public void EmptySources_ReturnsEmpty()
    {
        var userId = Guid.NewGuid();
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy() },
            DefaultPolicyId = "p1",
        });

        var result = QualityGateService.FilterMediaSources(userId, Array.Empty<MediaSourceInfo>()).ToList();
        Assert.Empty(result);
    }
}
