using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.EncodePriority;
using Jellyfin.Plugin.QualityGate.Tests.Harness;

namespace Jellyfin.Plugin.QualityGate.Tests;

/// <summary>
/// The encode priority settings through a real <c>XmlSerializer</c> across restarts, and the
/// snapshot that clamps them before anything reads them.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class EncodePriorityConfigTests : IDisposable
{
    private static readonly Guid ViewerA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ViewerB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid ViewerC = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private readonly ConfigRoundTrip _store = new();

    public void Dispose() => _store.Dispose();

    private static PluginConfiguration TwoTargets() => new()
    {
        EnableEncodePriority = true,
        PriorityNowPlaying = false,
        PriorityContinueWatchingPerUser = 7,
        PriorityNextUpDepth = 5,
        PriorityFavourites = true,
        PriorityUnprobedNeedsEncode = true,
        PriorityRefreshOnPlayback = false,
        PriorityDebounceMinutes = 3,
        PriorityRunBudgetSeconds = 60,
        EncodeTargets = new List<EncodeTarget>
        {
            new()
            {
                Id = "t-shows",
                Name = "Shows",
                Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = "/media/tv" } },
                AudienceMode = "Policies",
                AudiencePolicyIds = new List<string> { "p720", "p1080" },
                ExcludedUserIds = new List<Guid> { ViewerC },
            },
            new()
            {
                Id = "t-both",
                Name = "Both",
                Enabled = false,
                Folders = new List<EncodeFolderMapping>
                {
                    new() { JellyfinPath = "/media/movies", EncoderPath = "movies" },
                    new() { JellyfinPath = "/media/tv", EncoderPath = "tv" },
                },
                OutputHeight = 480,
                OutputMode = "Custom",
                OutputPath = "/config/lists/both.json",
                AudienceMode = "Users",
                AudienceUserIds = new List<Guid> { ViewerA, ViewerB },
                ResolveSymlinks = true,
                MaxEntries = 50,
                DryRun = true,
            },
        },
    };

    [Fact]
    public void TwoTargets_SurviveThreeRestarts_Unchanged()
    {
        var config = TwoTargets();

        for (var i = 0; i < 3; i++)
        {
            config = _store.SaveAndReload(config);
        }

        Assert.True(config.EnableEncodePriority);
        Assert.False(config.PriorityNowPlaying);
        Assert.Equal(7, config.PriorityContinueWatchingPerUser);
        Assert.Equal(5, config.PriorityNextUpDepth);
        Assert.True(config.PriorityFavourites);
        Assert.True(config.PriorityUnprobedNeedsEncode);
        Assert.False(config.PriorityRefreshOnPlayback);
        Assert.Equal(3, config.PriorityDebounceMinutes);
        Assert.Equal(60, config.PriorityRunBudgetSeconds);
        Assert.Equal(2, config.EncodeTargets.Count);

        var shows = config.EncodeTargets[0];
        Assert.Equal("t-shows", shows.Id);
        Assert.Equal("Shows", shows.Name);
        Assert.True(shows.Enabled);
        var folder = Assert.Single(shows.Folders);
        Assert.Equal("/media/tv", folder.JellyfinPath);
        Assert.Equal(string.Empty, folder.EncoderPath);
        Assert.Equal(720, shows.OutputHeight);
        Assert.Equal("SourceFolder", shows.OutputMode);
        Assert.Equal("Policies", shows.AudienceMode);
        Assert.Equal(new[] { "p720", "p1080" }, shows.AudiencePolicyIds);
        Assert.Empty(shows.AudienceUserIds);
        Assert.Equal(new[] { ViewerC }, shows.ExcludedUserIds);
        Assert.Equal(300, shows.MaxEntries);

        var both = config.EncodeTargets[1];
        Assert.False(both.Enabled);
        Assert.Equal(new[] { ("/media/movies", "movies"), ("/media/tv", "tv") }, both.Folders.Select(f => (f.JellyfinPath, f.EncoderPath)));
        Assert.Equal(480, both.OutputHeight);
        Assert.Equal("Custom", both.OutputMode);
        Assert.Equal("/config/lists/both.json", both.OutputPath);
        Assert.Equal(new[] { ViewerA, ViewerB }, both.AudienceUserIds);
        Assert.True(both.ResolveSymlinks);
        Assert.Equal(50, both.MaxEntries);
        Assert.True(both.DryRun);
    }

    /// <summary>
    /// Every list starts empty, so none grows across restarts and a cleared one stays cleared:
    /// the suffix list's bug cannot recur in the new settings.
    /// </summary>
    [Fact]
    public void EveryList_ClearedAfterBeingFull_StaysClearedAcrossRestarts()
    {
        var config = _store.SaveAndReload(TwoTargets());
        foreach (var target in config.EncodeTargets)
        {
            target.Folders = new List<EncodeFolderMapping>();
            target.AudiencePolicyIds = new List<string>();
            target.AudienceUserIds = new List<Guid>();
            target.ExcludedUserIds = new List<Guid>();
        }

        for (var i = 0; i < 3; i++)
        {
            config = _store.SaveAndReload(config);
            Assert.Equal(2, config.EncodeTargets.Count);
            Assert.All(config.EncodeTargets, t =>
            {
                Assert.Empty(t.Folders);
                Assert.Empty(t.AudiencePolicyIds);
                Assert.Empty(t.AudienceUserIds);
                Assert.Empty(t.ExcludedUserIds);
            });
        }

        config.EncodeTargets = new List<EncodeTarget>();
        for (var i = 0; i < 3; i++)
        {
            config = _store.SaveAndReload(config);
            Assert.Empty(config.EncodeTargets);
        }
    }

    [Fact]
    public void ConfigFromBeforeEncodePriority_LoadsWithTheFeatureOff()
    {
        _store.WriteXml("""
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <Policies />
              <UserPolicies />
              <DefaultPolicyId />
              <DefaultIntroVideoPath />
              <ApiKeyPolicyId />
              <EnableVersionGrouping>false</EnableVersionGrouping>
              <VersionGroupingRoots />
              <VersionGroupingSuffixes>
                <string> - 720p</string>
              </VersionGroupingSuffixes>
            </PluginConfiguration>
            """);

        var config = _store.Load();
        var options = EncodePriorityOptions.From(config);

        Assert.False(config.EnableEncodePriority);
        Assert.Empty(config.EncodeTargets);
        Assert.False(options.Enabled);
        Assert.Empty(options.Targets);
        Assert.Empty(options.Warnings);

        // The defaults the design names, not zeros.
        Assert.True(options.NowPlaying);
        Assert.True(options.ContinueWatching);
        Assert.Equal(20, options.ContinueWatchingPerUser);
        Assert.True(options.NextUp);
        Assert.Equal(3, options.NextUpDepth);
        Assert.Equal(10, options.NextUpShowsPerUser);
        Assert.False(options.NextUpIncludeSpecials);
        Assert.False(options.Favourites);
        Assert.Equal(25, options.FavouritesPerUser);
        Assert.Equal(30, options.WatchedWithinDays);
        Assert.False(options.UnprobedNeedsEncode);
        Assert.True(options.RefreshOnPlayback);
        Assert.Equal(TimeSpan.FromMinutes(10), options.Debounce);
        Assert.Equal(TimeSpan.FromSeconds(180), options.RunBudget);
    }

    [Fact]
    public void AFreshTarget_HasTheDesignDefaults()
    {
        var target = new EncodeTarget();

        Assert.False(string.IsNullOrEmpty(target.Id));
        Assert.True(target.Enabled);
        Assert.Empty(target.Folders);
        Assert.Equal(720, target.OutputHeight);
        Assert.Equal("SourceFolder", target.OutputMode);
        Assert.Equal("Auto", target.AudienceMode);
        Assert.Equal(300, target.MaxEntries);
        Assert.False(target.DryRun);
        Assert.False(target.ResolveSymlinks);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(20, 20)]
    [InlineData(101, 100)]
    public void Options_ClampContinueWatchingPerUser(int configured, int expected)
    {
        var options = EncodePriorityOptions.From(new PluginConfiguration { PriorityContinueWatchingPerUser = configured });

        Assert.Equal(expected, options.ContinueWatchingPerUser);
        Assert.Equal(configured == expected ? 0 : 1, options.Warnings.Count);
    }

    [Fact]
    public void Options_ClampEveryNumberIntoItsRange()
    {
        var options = EncodePriorityOptions.From(new PluginConfiguration
        {
            PriorityNextUpDepth = 0,
            PriorityNextUpShowsPerUser = 999,
            PriorityFavouritesPerUser = 0,
            PriorityWatchedWithinDays = 1000,
            PriorityDebounceMinutes = 0,
            PriorityRunBudgetSeconds = 5,
            EncodeTargets = new List<EncodeTarget>
            {
                new()
                {
                    Id = "t1",
                    Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = "/media/tv" } },
                    OutputHeight = 10,
                    MaxEntries = 0,
                },
                new()
                {
                    Id = "t2",
                    Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = "/media/tv" } },
                    OutputHeight = 99999,
                    MaxEntries = 99999,
                },
            },
        });

        Assert.Equal(1, options.NextUpDepth);
        Assert.Equal(50, options.NextUpShowsPerUser);
        Assert.Equal(1, options.FavouritesPerUser);
        Assert.Equal(365, options.WatchedWithinDays);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Debounce);
        Assert.Equal(TimeSpan.FromSeconds(10), options.RunBudget);
        Assert.Equal(144, options.Targets[0].OutputHeight);
        Assert.Equal(1, options.Targets[0].MaxEntries);
        Assert.Equal(4320, options.Targets[1].OutputHeight);
        Assert.Equal(5000, options.Targets[1].MaxEntries);
    }

    [Fact]
    public void Options_UnknownEnumStrings_FallBackToTheDefaultWithOneWarningEach()
    {
        var options = EncodePriorityOptions.From(new PluginConfiguration
        {
            EncodeTargets = new List<EncodeTarget>
            {
                new()
                {
                    Id = "t1",
                    Name = "Shows",
                    Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = "/media/tv" } },
                    OutputMode = "Nowhere",
                    AudienceMode = "2",
                },
            },
        });

        var target = Assert.Single(options.Targets);
        Assert.Equal(OutputMode.SourceFolder, target.OutputMode);
        Assert.Equal(AudienceMode.Auto, target.AudienceMode);
        Assert.Equal(2, options.Warnings.Count);
        Assert.Contains(options.Warnings, w => w.Contains("Nowhere", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("datafolder", "DataFolder")]
    [InlineData("Custom", "Custom")]
    [InlineData("", "SourceFolder")]
    public void Options_KnownEnumStrings_ParseWithoutWarnings(string value, string expected)
    {
        var options = EncodePriorityOptions.From(new PluginConfiguration
        {
            EncodeTargets = new List<EncodeTarget>
            {
                new() { Id = "t1", Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = "/media/tv" } }, OutputMode = value },
            },
        });

        Assert.Equal(expected, options.Targets[0].OutputMode.ToString());
        Assert.Empty(options.Warnings);
    }

    [Fact]
    public void Options_DropInvalidFoldersAndCleanTheRest()
    {
        var options = EncodePriorityOptions.From(new PluginConfiguration
        {
            EncodeTargets = new List<EncodeTarget>
            {
                new()
                {
                    Id = "t1",
                    Name = "Shows",
                    Folders = new List<EncodeFolderMapping>
                    {
                        new() { JellyfinPath = "/media/tv/", EncoderPath = "tv/" },
                        new() { JellyfinPath = "media/relative" },
                        new() { JellyfinPath = "/media/a", EncoderPath = "/abs" },
                        new() { JellyfinPath = "/media/b", EncoderPath = "../up" },
                        new() { JellyfinPath = "/media/c", EncoderPath = @"win\path" },
                        new() { JellyfinPath = "/" },
                    },
                },
            },
        });

        var target = Assert.Single(options.Targets);
        Assert.Equal(new[] { new FolderMapping("/media/tv", "tv") }, target.Folders);
        Assert.Equal(5, options.Warnings.Count);
    }

    [Fact]
    public void Options_AnEnabledTargetWithNoValidFolder_IsNotRunnable()
    {
        var options = EncodePriorityOptions.From(new PluginConfiguration
        {
            EncodeTargets = new List<EncodeTarget> { new() { Id = "t1", Name = "Empty" } },
        });

        var target = Assert.Single(options.Targets);
        Assert.False(target.IsRunnable);
        Assert.Empty(options.RunnableTargets);
        Assert.Single(options.Warnings);
    }

    [Fact]
    public void Options_RepeatedOrMissingIds_AreIgnoredAndTheCountIsCapped()
    {
        var targets = Enumerable.Range(0, 20)
            .Select(i => new EncodeTarget { Id = "t" + i, Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = "/media/tv" } } })
            .ToList();
        targets.Insert(0, new EncodeTarget { Id = "t0" });
        targets.Insert(0, new EncodeTarget { Id = " " });

        var options = EncodePriorityOptions.From(new PluginConfiguration { EncodeTargets = targets });

        Assert.Equal(EncodePriorityOptions.MaxTargets, options.Targets.Count);
        Assert.Equal("t0", options.Targets[0].Id);
        Assert.Empty(options.Targets[0].Folders);
        Assert.DoesNotContain(options.Targets.Skip(1), t => t.Id == "t0");
    }

    [Fact]
    public void Options_ANameIsFilledInAndTrimmedTo64Characters()
    {
        var options = EncodePriorityOptions.From(new PluginConfiguration
        {
            EncodeTargets = new List<EncodeTarget>
            {
                new() { Id = "t1", Name = "  " },
                new() { Id = "t2", Name = new string('x', 80) },
            },
        });

        Assert.Equal("Encoder 1", options.Targets[0].Name);
        Assert.Equal(64, options.Targets[1].Name.Length);
    }
}
