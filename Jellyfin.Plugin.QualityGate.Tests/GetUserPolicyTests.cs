using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Moq;

namespace Jellyfin.Plugin.QualityGate.Tests;

public class GetUserPolicyTests : IDisposable
{
    private readonly Plugin _plugin;
    private readonly string _tempDir;

    public GetUserPolicyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qg-up-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static QualityPolicy MakePolicy(string id = "p1", string name = "Test", bool enabled = true)
    {
        return new QualityPolicy
        {
            Id = id,
            Name = name,
            Enabled = enabled,
            AllowedFilenamePatterns = new List<string> { "- 720p" },
            BlockedFilenamePatterns = new List<string>(),
        };
    }

    [Fact]
    public void NoConfig_ReturnsNull()
    {
        SetConfig(new PluginConfiguration());
        var result = QualityGateService.GetUserPolicy(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public void UserWithFullAccessOverride_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy() },
            DefaultPolicyId = "p1",
            UserPolicies = new List<UserPolicyAssignment>
            {
                new() { UserId = userId, PolicyId = UserPolicyAssignment.FullAccessPolicyId },
            },
        });

        Assert.Null(QualityGateService.GetUserPolicy(userId));
    }

    [Fact]
    public void UserWithValidOverride_ReturnsThatPolicy()
    {
        var userId = Guid.NewGuid();
        var policy = MakePolicy("override-1", "Override Policy");
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(), policy },
            UserPolicies = new List<UserPolicyAssignment>
            {
                new() { UserId = userId, PolicyId = "override-1" },
            },
        });

        var result = QualityGateService.GetUserPolicy(userId);
        Assert.NotNull(result);
        Assert.Equal("override-1", result.Id);
        Assert.Equal("Override Policy", result.Name);
    }

    [Fact]
    public void UserWithInvalidOverride_ReturnsDenyAll()
    {
        var userId = Guid.NewGuid();
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy() },
            UserPolicies = new List<UserPolicyAssignment>
            {
                new() { UserId = userId, PolicyId = "deleted-policy" },
            },
        });

        var result = QualityGateService.GetUserPolicy(userId);
        Assert.NotNull(result);
        Assert.Equal(QualityGateService.DenyAllPolicy.Id, result.Id);
    }

    [Fact]
    public void UserWithDisabledOverride_ReturnsDenyAll()
    {
        var userId = Guid.NewGuid();
        var disabled = MakePolicy("disabled-1", "Disabled", enabled: false);
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { disabled },
            UserPolicies = new List<UserPolicyAssignment>
            {
                new() { UserId = userId, PolicyId = "disabled-1" },
            },
        });

        var result = QualityGateService.GetUserPolicy(userId);
        Assert.NotNull(result);
        Assert.Equal(QualityGateService.DenyAllPolicy.Id, result.Id);
    }

    [Fact]
    public void NoOverride_DefaultPolicyApplies()
    {
        var policy = MakePolicy("default-1", "Default");
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { policy },
            DefaultPolicyId = "default-1",
        });

        var result = QualityGateService.GetUserPolicy(Guid.NewGuid());
        Assert.NotNull(result);
        Assert.Equal("default-1", result.Id);
    }

    [Fact]
    public void InvalidDefaultPolicy_ReturnsDenyAll()
    {
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy() },
            DefaultPolicyId = "nonexistent",
        });

        var result = QualityGateService.GetUserPolicy(Guid.NewGuid());
        Assert.NotNull(result);
        Assert.Equal(QualityGateService.DenyAllPolicy.Id, result.Id);
    }

    [Fact]
    public void DisabledDefaultPolicy_ReturnsDenyAll()
    {
        var disabled = MakePolicy("dis-default", "Disabled Default", enabled: false);
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { disabled },
            DefaultPolicyId = "dis-default",
        });

        var result = QualityGateService.GetUserPolicy(Guid.NewGuid());
        Assert.NotNull(result);
        Assert.Equal(QualityGateService.DenyAllPolicy.Id, result.Id);
    }

    [Fact]
    public void UserOverride_TakesPriorityOverDefault()
    {
        var userId = Guid.NewGuid();
        var userPolicy = MakePolicy("user-pol", "User Policy");
        var defaultPolicy = MakePolicy("default-pol", "Default Policy");
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { userPolicy, defaultPolicy },
            DefaultPolicyId = "default-pol",
            UserPolicies = new List<UserPolicyAssignment>
            {
                new() { UserId = userId, PolicyId = "user-pol" },
            },
        });

        var result = QualityGateService.GetUserPolicy(userId);
        Assert.NotNull(result);
        Assert.Equal("user-pol", result.Id);
    }

    [Fact]
    public void DifferentUsers_GetDifferentPolicies()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();
        var policy1 = MakePolicy("pol-1", "Policy 1");
        var policy2 = MakePolicy("pol-2", "Policy 2");
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { policy1, policy2 },
            UserPolicies = new List<UserPolicyAssignment>
            {
                new() { UserId = user1, PolicyId = "pol-1" },
                new() { UserId = user2, PolicyId = "pol-2" },
            },
        });

        var result1 = QualityGateService.GetUserPolicy(user1);
        var result2 = QualityGateService.GetUserPolicy(user2);
        Assert.Equal("pol-1", result1!.Id);
        Assert.Equal("pol-2", result2!.Id);
    }

    [Fact]
    public void HasFullAccess_TrueWhenNoPolicy()
    {
        SetConfig(new PluginConfiguration());
        Assert.True(QualityGateService.HasFullAccess(Guid.NewGuid()));
    }

    [Fact]
    public void HasFullAccess_FalseWhenDefaultPolicySet()
    {
        var policy = MakePolicy("p1", "Default");
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { policy },
            DefaultPolicyId = "p1",
        });

        Assert.False(QualityGateService.HasFullAccess(Guid.NewGuid()));
    }
}
