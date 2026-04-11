using Jellyfin.Plugin.QualityGate.Configuration;
using Xunit;

namespace Jellyfin.Plugin.QualityGate.Tests;

public class ConfigurationTests
{
    [Fact]
    public void PluginConfiguration_DefaultValues()
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
    public void QualityPolicy_DefaultValues()
    {
        var policy = new QualityPolicy();

        Assert.NotNull(policy.Id);
        Assert.NotEmpty(policy.Id);
        Assert.Equal(string.Empty, policy.Name);
        Assert.Equal(string.Empty, policy.Description);
        Assert.NotNull(policy.AllowedPathPrefixes);
        Assert.Empty(policy.AllowedPathPrefixes);
        Assert.NotNull(policy.BlockedPathPrefixes);
        Assert.Empty(policy.BlockedPathPrefixes);
        Assert.NotNull(policy.AllowedFilenamePatterns);
        Assert.Empty(policy.AllowedFilenamePatterns);
        Assert.NotNull(policy.BlockedFilenamePatterns);
        Assert.Empty(policy.BlockedFilenamePatterns);
        Assert.True(policy.Enabled);
        Assert.Equal("Quality Restricted", policy.BlockedMessageHeader);
        Assert.Equal("This quality version is not available for your account.", policy.BlockedMessageText);
        Assert.Equal(8000, policy.BlockedMessageTimeoutMs);
        Assert.Equal(string.Empty, policy.IntroVideoPath);
    }

    [Fact]
    public void QualityPolicy_IdIsUniquePerInstance()
    {
        var policy1 = new QualityPolicy();
        var policy2 = new QualityPolicy();
        Assert.NotEqual(policy1.Id, policy2.Id);
    }

    [Fact]
    public void UserPolicyAssignment_DefaultValues()
    {
        var assignment = new UserPolicyAssignment();

        Assert.Equal(Guid.Empty, assignment.UserId);
        Assert.Equal(string.Empty, assignment.Username);
        Assert.Equal(string.Empty, assignment.PolicyId);
    }

    [Fact]
    public void UserPolicyAssignment_FullAccessConstant()
    {
        Assert.Equal("__FULL_ACCESS__", UserPolicyAssignment.FullAccessPolicyId);
    }

    [Fact]
    public void QualityPolicy_CanAddPathPrefixes()
    {
        var policy = new QualityPolicy();
        policy.AllowedPathPrefixes.Add("/media-transcoded/");
        policy.BlockedPathPrefixes.Add("/media/4K/");

        Assert.Single(policy.AllowedPathPrefixes);
        Assert.Single(policy.BlockedPathPrefixes);
        Assert.Equal("/media-transcoded/", policy.AllowedPathPrefixes[0]);
        Assert.Equal("/media/4K/", policy.BlockedPathPrefixes[0]);
    }

    [Fact]
    public void QualityPolicy_CanAddFilenamePatterns()
    {
        var policy = new QualityPolicy();
        policy.AllowedFilenamePatterns.Add("- 720p");
        policy.BlockedFilenamePatterns.Add("- 2160p");

        Assert.Single(policy.AllowedFilenamePatterns);
        Assert.Single(policy.BlockedFilenamePatterns);
    }
}
