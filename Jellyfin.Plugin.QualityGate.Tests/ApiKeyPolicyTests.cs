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
/// Covers the policy applied to requests that resolve to no user.
///
/// Jellyfin's API-key authentication issues <see cref="Guid.Empty"/> instead of a user id, so such
/// a request matches no assignment, takes no default, and was served with no cap at all. These
/// tests pin both halves of the fix: that the opt-in policy is applied when set, and that leaving
/// it unset keeps the previous behaviour so an upgrade cannot silently cap an integration.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public class ApiKeyPolicyTests : IDisposable
{
    private readonly Plugin _plugin;
    private readonly string _tempDir;

    public ApiKeyPolicyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qg-ak-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static QualityPolicy Policy(string id = "cap", int maxHeight = 720, bool enabled = true)
    {
        return new QualityPolicy
        {
            Id = id,
            Name = "Capped",
            Enabled = enabled,
            MaxHeight = maxHeight,
        };
    }

    private void Configure(string apiKeyPolicyId, params QualityPolicy[] policies)
    {
        _plugin.Configuration.Policies = new List<QualityPolicy>(policies);
        _plugin.Configuration.UserPolicies = new List<UserPolicyAssignment>();
        _plugin.Configuration.DefaultPolicyId = string.Empty;
        _plugin.Configuration.ApiKeyPolicyId = apiKeyPolicyId;
    }

    [Fact]
    public void UnsetByDefault_SoUpgradingChangesNothing()
    {
        Assert.Equal(string.Empty, new PluginConfiguration().ApiKeyPolicyId);
    }

    [Fact]
    public void NoApiKeyPolicy_LeavesTheRequestUncapped()
    {
        Configure(string.Empty, Policy());

        Assert.Null(QualityGateService.GetApiKeyPolicy());
    }

    [Fact]
    public void ConfiguredPolicy_IsReturned()
    {
        Configure("cap", Policy());

        var policy = QualityGateService.GetApiKeyPolicy();

        Assert.NotNull(policy);
        Assert.Equal("cap", policy!.Id);
        Assert.Equal(720, policy.MaxHeight);
        Assert.True(QualityGateService.HasHeightCap(policy));
    }

    [Fact]
    public void DisabledPolicy_Denies()
    {
        Configure("cap", Policy(enabled: false));

        Assert.True(QualityGateService.IsDenyAll(QualityGateService.GetApiKeyPolicy()));
    }

    [Fact]
    public void UnresolvablePolicy_Denies_LikeABrokenUserAssignment()
    {
        // The operator asked for these requests to be restricted. Serving them uncapped because the
        // policy went missing would widen the access they meant to narrow.
        Configure("does-not-exist", Policy());

        Assert.True(QualityGateService.IsDenyAll(QualityGateService.GetApiKeyPolicy()));
    }

    [Fact]
    public void ApiKeyPolicy_DoesNotLeakIntoNormalUserResolution()
    {
        Configure("cap", Policy());

        // A real user with no assignment and no default policy stays unrestricted; the API-key
        // setting must not act as a second default.
        Assert.Null(QualityGateService.GetUserPolicy(Guid.NewGuid()));
    }

    [Fact]
    public void UserAssignments_AreUnaffectedByTheApiKeySetting()
    {
        var userId = Guid.NewGuid();
        Configure("cap", Policy(), Policy("other", 1080));
        _plugin.Configuration.UserPolicies.Add(new UserPolicyAssignment
        {
            UserId = userId,
            PolicyId = "other",
        });

        var policy = QualityGateService.GetUserPolicy(userId);

        Assert.NotNull(policy);
        Assert.Equal("other", policy!.Id);
    }

    [Fact]
    public void ZeroHeightPolicy_ResolvesButCapsNothing()
    {
        // A policy is only enforced when it actually carries a height, so pointing API keys at one
        // with no cap must not start refusing their requests.
        Configure("cap", Policy(maxHeight: 0));

        var policy = QualityGateService.GetApiKeyPolicy();

        Assert.NotNull(policy);
        Assert.False(QualityGateService.HasHeightCap(policy));
    }
}
