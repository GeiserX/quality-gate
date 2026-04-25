using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Moq;

namespace Jellyfin.Plugin.QualityGate.Tests;

/// <summary>
/// Additional tests for QualityGateService to cover remaining edge cases.
/// </summary>
public class QualityGateServiceAdditionalTests : IDisposable
{
    private readonly Plugin _plugin;
    private readonly string _tempDir;

    public QualityGateServiceAdditionalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qg-svc-" + Guid.NewGuid().ToString("N")[..8]);
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

    private void SetConfig(PluginConfiguration config)
    {
        _plugin.Configuration.Policies = config.Policies;
        _plugin.Configuration.UserPolicies = config.UserPolicies;
        _plugin.Configuration.DefaultPolicyId = config.DefaultPolicyId;
    }

    [Fact]
    public void ResolvePath_RegularFile_ReturnsSamePath()
    {
        var path = Path.Combine(_tempDir, "test.mkv");
        File.WriteAllText(path, "test");
        var result = QualityGateService.ResolvePath(path);
        Assert.Equal(path, result);
    }

    [Fact]
    public void ResolvePath_NonExistentFile_ReturnsSamePath()
    {
        var path = "/nonexistent/path/file.mkv";
        var result = QualityGateService.ResolvePath(path);
        Assert.Equal(path, result);
    }

    [Fact]
    public void ResolvePath_Symlink_ReturnsTarget()
    {
        var targetPath = Path.Combine(_tempDir, "target.mkv");
        File.WriteAllText(targetPath, "target");
        var linkPath = Path.Combine(_tempDir, "link.mkv");

        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            var result = QualityGateService.ResolvePath(linkPath);
            Assert.Equal(targetPath, result);
        }
        catch (UnauthorizedAccessException)
        {
            // Symlink creation may require elevated privileges
        }
    }

    [Fact]
    public void DenyAllPolicy_HasExpectedProperties()
    {
        var policy = QualityGateService.DenyAllPolicy;
        Assert.Equal("__DENY_ALL__", policy.Id);
        Assert.True(policy.Enabled);
        Assert.NotEmpty(policy.AllowedFilenamePatterns);
        Assert.Contains("^$", policy.AllowedFilenamePatterns);
    }

    [Fact]
    public void GetUserPolicy_UserAssignment_EmptyPolicyId_FallsToDefault()
    {
        var userId = Guid.NewGuid();
        var defaultPolicy = new QualityPolicy
        {
            Id = "default",
            Name = "Default",
            Enabled = true,
            AllowedFilenamePatterns = new List<string> { "- 720p" },
            BlockedFilenamePatterns = new List<string>(),
        };

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { defaultPolicy },
            DefaultPolicyId = "default",
            UserPolicies = new List<UserPolicyAssignment>
            {
                new() { UserId = userId, PolicyId = string.Empty },
            },
        });

        var result = QualityGateService.GetUserPolicy(userId);
        Assert.NotNull(result);
        Assert.Equal("default", result.Id);
    }

    [Fact]
    public void CanAccessPath_NoPolicy_AllowsAccess()
    {
        SetConfig(new PluginConfiguration());
        Assert.True(QualityGateService.CanAccessPath(Guid.NewGuid(), "/any/path.mkv"));
    }

    [Fact]
    public void HasFullAccess_FullAccessOverride_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                new QualityPolicy
                {
                    Id = "p1",
                    Name = "Test",
                    Enabled = true,
                    AllowedFilenamePatterns = new List<string> { "- 720p" },
                    BlockedFilenamePatterns = new List<string>(),
                },
            },
            DefaultPolicyId = "p1",
            UserPolicies = new List<UserPolicyAssignment>
            {
                new() { UserId = userId, PolicyId = UserPolicyAssignment.FullAccessPolicyId },
            },
        });

        Assert.True(QualityGateService.HasFullAccess(userId));
    }

    [Fact]
    public void PluginConfiguration_DefaultValues_AreCorrect()
    {
        var config = new PluginConfiguration();
        Assert.NotNull(config.Policies);
        Assert.Empty(config.Policies);
        Assert.NotNull(config.UserPolicies);
        Assert.Empty(config.UserPolicies);
        Assert.Equal(string.Empty, config.DefaultPolicyId);
        Assert.Equal(string.Empty, config.DefaultIntroVideoPath);
    }

    [Fact]
    public void QualityPolicy_DefaultValues_AreCorrect()
    {
        var policy = new QualityPolicy();
        Assert.NotNull(policy.Id);
        Assert.NotEmpty(policy.Id);
        Assert.Equal(string.Empty, policy.Name);
        Assert.Equal(string.Empty, policy.Description);
        Assert.NotNull(policy.AllowedFilenamePatterns);
        Assert.Empty(policy.AllowedFilenamePatterns);
        Assert.NotNull(policy.BlockedFilenamePatterns);
        Assert.Empty(policy.BlockedFilenamePatterns);
        Assert.True(policy.Enabled);
        Assert.Equal("Quality Restricted", policy.BlockedMessageHeader);
        Assert.False(string.IsNullOrEmpty(policy.BlockedMessageText));
        Assert.Equal(8000, policy.BlockedMessageTimeoutMs);
        Assert.Equal(string.Empty, policy.IntroVideoPath);
        Assert.False(policy.FallbackTranscode);
        Assert.Equal(0, policy.FallbackMaxHeight);
        Assert.Equal(0, policy.FallbackMaxBitrateKbps);
    }

    [Fact]
    public void UserPolicyAssignment_DefaultValues_AreCorrect()
    {
        var assignment = new UserPolicyAssignment();
        Assert.Equal(Guid.Empty, assignment.UserId);
        Assert.Equal(string.Empty, assignment.Username);
        Assert.Equal(string.Empty, assignment.PolicyId);
    }

    [Fact]
    public void UserPolicyAssignment_FullAccessPolicyId_IsExpected()
    {
        Assert.Equal("__FULL_ACCESS__", UserPolicyAssignment.FullAccessPolicyId);
    }
}
