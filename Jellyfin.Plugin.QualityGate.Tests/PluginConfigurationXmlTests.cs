using System;
using System.Collections.Generic;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Tests.Harness;

namespace Jellyfin.Plugin.QualityGate.Tests;

/// <summary>
/// Saves and reloads the plugin configuration through a real <c>XmlSerializer</c>, the way a
/// server does across a restart. Every other test hands the plugin a mocked serializer, which
/// cannot show what the XML file does to a value.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class PluginConfigurationXmlTests : IDisposable
{
    private readonly ConfigRoundTrip _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void PoliciesAndAssignments_SurviveRepeatedRestarts_Unchanged()
    {
        var userId = Guid.NewGuid();
        var config = new PluginConfiguration
        {
            DefaultPolicyId = "p1",
            EnableVersionGrouping = true,
            VersionGroupingRoots = new List<string> { "/media/movies" },
            Policies = new List<QualityPolicy>
            {
                new()
                {
                    Id = "p1",
                    Name = "Capped",
                    MaxHeight = 720,
                    AllowedFilenamePatterns = new List<string> { "- 720p" },
                },
            },
            UserPolicies = new List<UserPolicyAssignment>
            {
                new() { UserId = userId, Username = "viewer", PolicyId = "p1" },
            },
        };

        for (var i = 0; i < 3; i++)
        {
            config = _store.SaveAndReload(config);
        }

        Assert.Equal("p1", config.DefaultPolicyId);
        Assert.True(config.EnableVersionGrouping);
        Assert.Equal(new[] { "/media/movies" }, config.VersionGroupingRoots);
        var policy = Assert.Single(config.Policies);
        Assert.Equal(720, policy.MaxHeight);
        Assert.Equal(new[] { "- 720p" }, policy.AllowedFilenamePatterns);
        var assignment = Assert.Single(config.UserPolicies);
        Assert.Equal(userId, assignment.UserId);
    }
}
