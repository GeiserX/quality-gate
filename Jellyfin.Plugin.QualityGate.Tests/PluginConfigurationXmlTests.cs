using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Library;
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

    // XmlSerializer adds loaded items to a list its initialiser already filled, so a list that
    // starts with the default gains another copy of it on every save and restart.
    [Fact]
    public void GroupingSuffixes_DoNotGrowAcrossRestarts()
    {
        var config = new PluginConfiguration { VersionGroupingSuffixes = new List<string> { " - 720p" } };

        for (var i = 0; i < 3; i++)
        {
            config = _store.SaveAndReload(config);
            Assert.Equal(new[] { " - 720p" }, config.VersionGroupingSuffixes);
        }
    }

    [Fact]
    public void UntouchedGroupingSuffixes_DoNotGrowAcrossRestarts()
    {
        var config = new PluginConfiguration();

        for (var i = 0; i < 3; i++)
        {
            config = _store.SaveAndReload(config);
            Assert.Empty(config.VersionGroupingSuffixes);
            Assert.Equal(new[] { " - 720p" }, VersionGroupingResolver.EffectiveSuffixes(config));
        }
    }

    [Fact]
    public void ClearedGroupingSuffixes_StayCleared()
    {
        var config = _store.SaveAndReload(new PluginConfiguration { VersionGroupingSuffixes = new List<string> { " - SD" } });
        config.VersionGroupingSuffixes = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            config = _store.SaveAndReload(config);
            Assert.Empty(config.VersionGroupingSuffixes);
        }
    }

    [Fact]
    public void SavedGroupingSuffixes_AreKeptExactly()
    {
        var config = new PluginConfiguration { VersionGroupingSuffixes = new List<string> { " - SD", " - 720p" } };

        for (var i = 0; i < 3; i++)
        {
            config = _store.SaveAndReload(config);
        }

        Assert.Equal(new[] { " - SD", " - 720p" }, config.VersionGroupingSuffixes);
        Assert.Equal(new[] { " - SD", " - 720p" }, VersionGroupingResolver.EffectiveSuffixes(config));
    }

    [Fact]
    public void OneSavedCustomSuffix_IsUsedInsteadOfTheDefault()
    {
        // A single saved suffix is a real choice, not an empty list: the default must not replace it.
        var config = new PluginConfiguration { VersionGroupingSuffixes = new List<string> { " - SD" } };

        for (var i = 0; i < 3; i++)
        {
            config = _store.SaveAndReload(config);
        }

        Assert.Equal(new[] { " - SD" }, config.VersionGroupingSuffixes);
        Assert.Equal(new[] { " - SD" }, VersionGroupingResolver.EffectiveSuffixes(config));
    }

    [Fact]
    public void ConfigWrittenByTheCurrentRelease_LoadsWithTheSameEffectiveSuffixes()
    {
        _store.WriteXml(ReleaseXml("""
              <VersionGroupingSuffixes>
                <string> - 720p</string>
              </VersionGroupingSuffixes>
            """));

        var config = _store.Load();

        Assert.Equal(new[] { " - 720p" }, config.VersionGroupingSuffixes);
        Assert.Equal(new[] { " - 720p" }, VersionGroupingResolver.EffectiveSuffixes(config));
    }

    [Fact]
    public void ConfigAlreadyGrownByTheCurrentRelease_IsKeptAsSaved()
    {
        _store.WriteXml(ReleaseXml("""
              <VersionGroupingSuffixes>
                <string> - 720p</string>
                <string> - 720p</string>
              </VersionGroupingSuffixes>
            """));

        var config = _store.Load();

        // The copies are left for the admin to tidy; a repeated suffix never changes a pairing.
        Assert.Equal(new[] { " - 720p", " - 720p" }, config.VersionGroupingSuffixes);
        Assert.Equal(new[] { " - 720p" }, VersionGroupingResolver.EffectiveSuffixes(config).Distinct());
    }

    [Fact]
    public void ConfigFromBeforeVersionGrouping_UsesTheDefaultSuffix()
    {
        _store.WriteXml(ReleaseXml(string.Empty));

        var config = _store.Load();

        Assert.Empty(config.VersionGroupingSuffixes);
        Assert.Equal(new[] { " - 720p" }, VersionGroupingResolver.EffectiveSuffixes(config));
    }

    [Fact]
    public void FreshInstall_UsesTheDefaultSuffix()
    {
        var config = _store.Load();

        Assert.Empty(config.VersionGroupingSuffixes);
        Assert.Equal(new[] { " - 720p" }, VersionGroupingResolver.EffectiveSuffixes(config));
    }

    /// <summary>A configuration file shaped the way release 3.8.2.0 writes it, with one policy.</summary>
    private static string ReleaseXml(string suffixElement) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <Policies>
            <QualityPolicy>
              <Id>p1</Id>
              <Name>Capped</Name>
              <Description />
              <AllowedFilenamePatterns />
              <BlockedFilenamePatterns />
              <MaxHeight>720</MaxHeight>
              <Enabled>true</Enabled>
              <BlockedMessageHeader>Quality Restricted</BlockedMessageHeader>
              <BlockedMessageText>This quality version is not available for your account.</BlockedMessageText>
              <BlockedMessageTimeoutMs>8000</BlockedMessageTimeoutMs>
              <IntroVideoPath />
              <FallbackTranscode>false</FallbackTranscode>
              <FallbackMaxHeight>0</FallbackMaxHeight>
              <FallbackMaxBitrateKbps>0</FallbackMaxBitrateKbps>
            </QualityPolicy>
          </Policies>
          <UserPolicies />
          <DefaultPolicyId>p1</DefaultPolicyId>
          <DefaultIntroVideoPath />
          <ApiKeyPolicyId />
          <EnableVersionGrouping>true</EnableVersionGrouping>
          <VersionGroupingRoots />
        {suffixElement}
        </PluginConfiguration>
        """;
}
