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

public class FallbackTranscodeTests : IDisposable
{
    private readonly Plugin _plugin;
    private readonly string _tempDir;

    public FallbackTranscodeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qg-fb-" + Guid.NewGuid().ToString("N")[..8]);
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
        bool fallbackTranscode = false)
    {
        return new QualityPolicy
        {
            Id = id,
            Name = "Test Policy",
            Enabled = true,
            AllowedFilenamePatterns = allowedPatterns ?? new List<string>(),
            BlockedFilenamePatterns = new List<string>(),
            FallbackTranscode = fallbackTranscode,
        };
    }

    private static MediaSourceInfo MakeSource(string path)
    {
        return new MediaSourceInfo
        {
            Path = path,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
        };
    }

    // --- ShouldFallbackTranscode tests ---

    [Fact]
    public void ShouldFallback_Disabled_ReturnsFalse()
    {
        var policy = MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: false);
        var sources = new[] { MakeSource("/media/Movie - 1080p.mkv") };
        Assert.False(QualityGateService.ShouldFallbackTranscode(policy, sources));
    }

    [Fact]
    public void ShouldFallback_Enabled_AllBlocked_ReturnsTrue()
    {
        var path = CreateTempFile("Movie - 1080p.mkv");
        var policy = MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true);
        var sources = new[] { MakeSource(path) };
        // 1080p doesn't match "- 720p" pattern, but file exists → all blocked by pattern
        // IsSourcePlayable checks pattern AND file existence, so this returns false for the source
        Assert.True(QualityGateService.ShouldFallbackTranscode(policy, sources));
    }

    [Fact]
    public void ShouldFallback_Enabled_SomeAllowed_ReturnsFalse()
    {
        var path720 = CreateTempFile("Movie - 720p.mkv");
        var path1080 = CreateTempFile("Movie - 1080p.mkv");
        var policy = MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true);
        var sources = new[] { MakeSource(path720), MakeSource(path1080) };
        Assert.False(QualityGateService.ShouldFallbackTranscode(policy, sources));
    }

    [Fact]
    public void ShouldFallback_EmptySources_ReturnsFalse()
    {
        var policy = MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true);
        Assert.False(QualityGateService.ShouldFallbackTranscode(policy, Array.Empty<MediaSourceInfo>()));
    }

    [Fact]
    public void ShouldFallback_DenyAllPolicy_ReturnsFalse()
    {
        // DenyAllPolicy is a misconfiguration sentinel — fallback must NEVER apply
        var path = CreateTempFile("Movie.mkv");
        var sources = new[] { MakeSource(path) };
        Assert.False(QualityGateService.ShouldFallbackTranscode(QualityGateService.DenyAllPolicy, sources));
    }

    // --- ApplyFallbackTranscode tests ---

    [Fact]
    public void ApplyFallback_DisablesDirectPlayAndStream()
    {
        var sources = new[]
        {
            MakeSource("/media/Movie.mkv"),
            MakeSource("/media/Movie - 2160p.mkv"),
        };

        var result = QualityGateService.ApplyFallbackTranscode(sources);

        Assert.Equal(2, result.Length);
        Assert.All(result, s =>
        {
            Assert.False(s.SupportsDirectPlay);
            Assert.False(s.SupportsDirectStream);
        });
    }

    [Fact]
    public void ApplyFallback_PreservesPath()
    {
        var sources = new[] { MakeSource("/media/Movie - Remux.mkv") };
        var result = QualityGateService.ApplyFallbackTranscode(sources);
        Assert.Single(result);
        Assert.Equal("/media/Movie - Remux.mkv", result[0].Path);
    }

    [Fact]
    public void ApplyFallback_EmptyInput_ReturnsEmpty()
    {
        var result = QualityGateService.ApplyFallbackTranscode(Array.Empty<MediaSourceInfo>());
        Assert.Empty(result);
    }

    [Fact]
    public void ApplyFallback_DoesNotMutateOriginals()
    {
        var sources = new[]
        {
            MakeSource("/media/Movie - 1080p.mkv"),
            MakeSource("/media/Movie - Remux.mkv"),
        };

        var result = QualityGateService.ApplyFallbackTranscode(sources);

        // Result should have flags disabled
        Assert.All(result, s =>
        {
            Assert.False(s.SupportsDirectPlay);
            Assert.False(s.SupportsDirectStream);
        });

        // Originals must remain untouched
        Assert.All(sources, s =>
        {
            Assert.True(s.SupportsDirectPlay);
            Assert.True(s.SupportsDirectStream);
        });
    }

    [Fact]
    public void ShouldFallback_AllSourcesMissing_ReturnsFalse()
    {
        // When all source files are dangling/missing, fallback should NOT trigger
        // because there's nothing to transcode
        var policy = MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true);
        var sources = new[] { MakeSource("/nonexistent/Movie - 4K.mkv") };
        Assert.False(QualityGateService.ShouldFallbackTranscode(policy, sources));
    }

    [Fact]
    public void PolicyAllowsFallback_Enabled_ReturnsTrue()
    {
        var policy = MakePolicy(fallbackTranscode: true);
        Assert.True(QualityGateService.PolicyAllowsFallback(policy));
    }

    [Fact]
    public void PolicyAllowsFallback_Disabled_ReturnsFalse()
    {
        var policy = MakePolicy(fallbackTranscode: false);
        Assert.False(QualityGateService.PolicyAllowsFallback(policy));
    }

    [Fact]
    public void PolicyAllowsFallback_DenyAllPolicy_ReturnsFalse()
    {
        Assert.False(QualityGateService.PolicyAllowsFallback(QualityGateService.DenyAllPolicy));
    }

    // --- Integration: FilterMediaSources with fallback ---

    [Fact]
    public void FilterMediaSources_FallbackEnabled_NoMatch_ReturnsAllWithTranscode()
    {
        var userId = Guid.NewGuid();
        var pathRemux = CreateTempFile("Movie [BDRemux].mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true),
            },
            DefaultPolicyId = "p1",
        });

        var sources = new[] { MakeSource(pathRemux) };
        var result = QualityGateService.FilterMediaSources(userId, sources).ToList();

        // FilterMediaSources itself doesn't apply fallback — that's the filter's job.
        // The service method returns empty when nothing matches.
        Assert.Empty(result);

        // But ShouldFallbackTranscode returns true for the caller to act on
        var policy = QualityGateService.GetUserPolicy(userId)!;
        Assert.True(QualityGateService.ShouldFallbackTranscode(policy, sources));
    }

    [Fact]
    public void FilterMediaSources_FallbackEnabled_HasMatch_ReturnsOnlyMatched()
    {
        var userId = Guid.NewGuid();
        var path720 = CreateTempFile("Movie - 720p.mkv");
        var pathRemux = CreateTempFile("Movie [BDRemux].mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true),
            },
            DefaultPolicyId = "p1",
        });

        var sources = new[] { MakeSource(path720), MakeSource(pathRemux) };
        var result = QualityGateService.FilterMediaSources(userId, sources).ToList();

        // When a match exists, normal filtering applies — only 720p returned
        Assert.Single(result);
        Assert.Equal(path720, result[0].Path);
        // Direct play should still be enabled on the matched source
        Assert.True(result[0].SupportsDirectPlay);
    }
}
