using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.EncodePriority;

namespace Jellyfin.Plugin.QualityGate.Tests;

/// <summary>
/// The list builder is pure: given who counts and what they asked for, it decides what each
/// encoder should make first. Every case here is plain data in, list out.
/// </summary>
public class PriorityListBuilderTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);

    private static readonly Viewer V480 = new(Guid.Parse("00000000-0000-0000-0000-000000000480"), false, Now, 480, "p480", false);
    private static readonly Viewer V720 = new(Guid.Parse("00000000-0000-0000-0000-000000000720"), false, Now, 720, "p720", false);
    private static readonly Viewer V720b = new(Guid.Parse("00000000-0000-0000-0000-000000007202"), false, Now, 720, "p720", false);
    private static readonly Viewer V1080 = new(Guid.Parse("00000000-0000-0000-0000-000000001080"), false, Now, 1080, "p1080", false);
    private static readonly Viewer Uncapped = new(Guid.Parse("00000000-0000-0000-0000-00000000000f"), false, Now, 0, null, false);
    private static readonly Viewer DenyAll = new(Guid.Parse("00000000-0000-0000-0000-0000000000dd"), false, Now, 0, "__DENY_ALL__", true);
    private static readonly Viewer Disabled = new(Guid.Parse("00000000-0000-0000-0000-0000000000d1"), true, Now, 720, "p720", false);
    private static readonly Viewer Idle = new(Guid.Parse("00000000-0000-0000-0000-0000000000d2"), false, Now.AddDays(-40), 720, "p720", false);

    private static readonly Viewer[] Everyone = { V480, V720, V720b, V1080, Uncapped, DenyAll, Disabled, Idle };

    private static EncodeTargetOptions Target(Action<EncodeTarget>? edit = null)
    {
        var target = new EncodeTarget
        {
            Id = "t1",
            Name = "Shows",
            Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = "/media/tv" } },
        };
        edit?.Invoke(target);
        return Options(target).Targets[0];
    }

    private static EncodePriorityOptions Options(params EncodeTarget[] targets) => Options(null, targets);

    private static EncodePriorityOptions Options(Action<PluginConfiguration>? edit, params EncodeTarget[] targets)
    {
        var config = new PluginConfiguration { EnableEncodePriority = true, EncodeTargets = targets.ToList() };
        edit?.Invoke(config);
        return EncodePriorityOptions.From(config);
    }

    private sealed class Demand
    {
        public CollectedDemand Collected { get; } = new();

        public Guid Item(string path, params int?[] heights)
        {
            var id = Guid.NewGuid();
            Collected.Versions[id] = heights
                .Select((h, i) => new VersionInfo(i == 0 ? id : Guid.NewGuid(), i == 0 ? path : path.Replace(".mkv", $" - v{i}.mkv", StringComparison.Ordinal), h))
                .ToList();
            return id;
        }

        public Demand Ask(Guid item, Viewer viewer, int tier = DemandTier.NextUp, int depth = 1, DateTime? at = null)
        {
            Collected.Signals.Add(new DemandSignal(item, viewer.Id, tier, depth, at ?? Now));
            return this;
        }
    }

    private static TargetBuild Build(EncodeTargetOptions target, Demand demand, EncodePriorityOptions? options = null, IReadOnlyList<LibraryFolder>? libraries = null, Func<string, string?>? resolveLink = null)
    {
        return PriorityListBuilder.Build(target, new BuildInput
        {
            Options = options ?? Options(),
            Viewers = Everyone,
            Demand = demand.Collected,
            Libraries = libraries ?? new[] { new LibraryFolder("Shows", new[] { "/media/tv" }) },
            Policies = new[] { new PolicySummary("Mobile", 480), new PolicySummary("HD", 720) },
            NowUtc = Now,
            ResolveLink = resolveLink ?? (p => p),
        });
    }

    // --- audience ---

    [Fact]
    public void Audience_Auto_IsCappedViewersAtOrAboveTheOutputHeight()
    {
        var audience = PriorityListBuilder.ResolveAudience(Target(), Everyone, Options(), Now, out _);

        Assert.Equal(new[] { V720.Id, V720b.Id, V1080.Id }.OrderBy(x => x), audience.Keys.OrderBy(x => x));
        Assert.Equal(1080, audience[V1080.Id]);
    }

    [Fact]
    public void Audience_ExcludedUsers_NeverCount()
    {
        var audience = PriorityListBuilder.ResolveAudience(Target(t => t.ExcludedUserIds.Add(V720.Id)), Everyone, Options(), Now, out _);

        Assert.DoesNotContain(V720.Id, audience.Keys);
    }

    [Fact]
    public void Audience_Policies_IsAutoLimitedToTheChosenPolicies()
    {
        var target = Target(t =>
        {
            t.AudienceMode = "Policies";
            t.AudiencePolicyIds.Add("p1080");
            t.AudiencePolicyIds.Add("p480");
        });

        var audience = PriorityListBuilder.ResolveAudience(target, Everyone, Options(), Now, out _);

        // p480 is chosen but capped below the 720p output, so it still cannot count.
        Assert.Equal(new[] { V1080.Id }, audience.Keys);
    }

    [Fact]
    public void Audience_Users_TreatsAnUncappedChoiceAsCappedAtTheOutputAndSkipsLowerCaps()
    {
        var target = Target(t =>
        {
            t.AudienceMode = "Users";
            t.AudienceUserIds.AddRange(new[] { Uncapped.Id, V480.Id, V1080.Id, DenyAll.Id });
        });

        var audience = PriorityListBuilder.ResolveAudience(target, Everyone, Options(), Now, out var belowOutput);

        Assert.Equal(720, audience[Uncapped.Id]);
        Assert.Equal(1080, audience[V1080.Id]);
        Assert.DoesNotContain(V480.Id, audience.Keys);
        Assert.DoesNotContain(DenyAll.Id, audience.Keys);
        Assert.Equal(1, belowOutput);
    }

    [Fact]
    public void Audience_NeverIncludesDenyAllDisabledOrIdleUsers()
    {
        foreach (var mode in new[] { "Auto", "Policies", "Users" })
        {
            var target = Target(t =>
            {
                t.AudienceMode = mode;
                t.AudiencePolicyIds.Add("p720");
                t.AudiencePolicyIds.Add("__DENY_ALL__");
                t.AudienceUserIds.AddRange(new[] { DenyAll.Id, Disabled.Id, Idle.Id });
            });

            var audience = PriorityListBuilder.ResolveAudience(target, Everyone, Options(), Now, out _);

            Assert.DoesNotContain(DenyAll.Id, audience.Keys);
            Assert.DoesNotContain(Disabled.Id, audience.Keys);
            Assert.DoesNotContain(Idle.Id, audience.Keys);
        }
    }

    [Fact]
    public void Build_NoAudience_IsAFindingNamingThePolicies()
    {
        var build = Build(Target(t => t.OutputHeight = 2160), new Demand());

        var finding = Assert.Single(build.Findings, f => f.Code == "NoAudience");
        Assert.Contains("2160p", finding.Message, StringComparison.Ordinal);
        Assert.Contains("Mobile (480p)", finding.Message, StringComparison.Ordinal);
        Assert.Empty(build.Entries);
    }

    // --- gaps ---

    [Fact]
    public void Gap_IsTestedAtEachViewersOwnCap()
    {
        var demand = new Demand();
        var only1080 = demand.Item("/media/tv/Show A/Season 1/Show A S01E01.mkv", 1080);
        demand.Ask(only1080, V1080).Ask(only1080, V720);
        var only2160 = demand.Item("/media/tv/Show A/Season 1/Show A S01E02.mkv", 2160);
        demand.Ask(only2160, V1080);

        var build = Build(Target(), demand);

        Assert.Equal(new[] { "Show A/Season 1/Show A S01E01.mkv", "Show A/Season 1/Show A S01E02.mkv" }.OrderBy(x => x), build.Paths.OrderBy(x => x));
        Assert.Equal(720, build.Entries.Single(e => e.ItemId == only1080).GapCap);
        Assert.Equal(1080, build.Entries.Single(e => e.ItemId == only2160).GapCap);
    }

    /// <summary>
    /// A 2160p-only file watched by a 1080p viewer is a gap only at 1080, so it is listed for every
    /// encoder whose output fits under 1080 and for none above it.
    /// </summary>
    [Fact]
    public void Gap_A2160pOnlyItemForA1080pViewer_IsListedOnlyWhereTheOutputFitsTheCap()
    {
        var demand = new Demand();
        var item = demand.Item("/media/tv/Show A/Season 1/Show A S01E01.mkv", 2160);
        demand.Ask(item, V1080);

        Assert.Single(Build(Target(t => t.OutputHeight = 1080), demand).Entries);
        Assert.Single(Build(Target(t => t.OutputHeight = 720), demand).Entries);
        Assert.Single(Build(Target(t => t.OutputHeight = 480), demand).Entries);
        Assert.Empty(Build(Target(t => t.OutputHeight = 1440), demand).Entries);
    }

    /// <summary>A 720p copy is still over a 480p viewer's cap, so that viewer drives only the 480p encoder.</summary>
    [Fact]
    public void Gap_AnItemOnlyA480pViewerWants_IsListedFor480pNotFor720p()
    {
        var demand = new Demand();
        var item = demand.Item("/media/tv/Show A/Season 1/Show A S01E01.mkv", 1080);
        demand.Ask(item, V480);

        Assert.Single(Build(Target(t => t.OutputHeight = 480), demand).Entries);
        Assert.Empty(Build(Target(t => t.OutputHeight = 720), demand).Entries);
    }

    [Fact]
    public void Gap_AnItemWithAWithinCapVersion_IsCoveredAndNotListed()
    {
        var demand = new Demand();
        var item = demand.Item("/media/tv/Show A/Season 1/Show A S01E01.mkv", 2160, 720);
        demand.Ask(item, V720);

        var build = Build(Target(), demand);

        Assert.Empty(build.Entries);
        Assert.Equal(0, build.Counts.Gaps);
        Assert.Equal(1, build.Counts.DemandItems);
    }

    [Fact]
    public void Gap_UnknownHeights_AreNotGapsByDefaultButAreCounted()
    {
        var demand = new Demand();
        var item = demand.Item("/media/tv/Show A/Season 1/Show A S01E01.mkv", new int?[] { null });
        demand.Ask(item, V720);

        var build = Build(Target(), demand);
        Assert.Empty(build.Entries);
        Assert.Equal(1, build.Counts.HeightUnknown);
        Assert.Contains(build.Findings, f => f.Code == "UnknownHeights");

        var flagged = Build(Target(), demand, Options(c => c.PriorityUnprobedNeedsEncode = true, new EncodeTarget
        {
            Id = "t1",
            Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = "/media/tv" } },
        }));
        Assert.Single(flagged.Entries);
    }

    [Fact]
    public void Sources_AreEveryVersionAboveTheOutput_LowestFirst()
    {
        var demand = new Demand();
        var item = demand.Item("/media/tv/Show A/Season 1/Show A S01E01.mkv", 2160, 1080);
        demand.Ask(item, V720);

        var build = Build(Target(), demand);

        Assert.Equal(new int?[] { 1080, 2160 }, build.Entries.Select(e => e.Height));
    }

    // --- mapping ---

    [Fact]
    public void Mapping_UsesTheLongestFolderAndItsEncoderPath()
    {
        var target = Target(t => t.Folders = new List<EncodeFolderMapping>
        {
            new() { JellyfinPath = "/media", EncoderPath = "all" },
            new() { JellyfinPath = "/media/tv", EncoderPath = "tv" },
        });

        Assert.Equal("tv/Show A/x.mkv", PriorityListBuilder.MapToEncoderPath("/media/tv/Show A/x.mkv", target.Folders));
        Assert.Equal("all/movies/Film A (2001).mkv", PriorityListBuilder.MapToEncoderPath("/media/movies/Film A (2001).mkv", target.Folders));
        Assert.Null(PriorityListBuilder.MapToEncoderPath("/srv/x.mkv", target.Folders));
        Assert.Null(PriorityListBuilder.MapToEncoderPath("/media/tv", target.Folders));
    }

    [Fact]
    public void Mapping_KeepsThePathByteIdentical()
    {
        var target = Target();
        const string decomposed = "Café Show/Season 1/x.mkv";

        Assert.Equal(decomposed, PriorityListBuilder.MapToEncoderPath("/media/tv/" + decomposed, target.Folders));
    }

    [Fact]
    public void Unmapped_GapsAreCountedWithFiveExamples()
    {
        var demand = new Demand();
        for (var i = 0; i < 7; i++)
        {
            demand.Ask(demand.Item($"/media/links/Show {i}/x.mkv", 1080), V720);
        }

        demand.Ask(demand.Item("/media/tv/Show A/x.mkv", 1080), V720);

        var build = Build(
            Target(),
            demand,
            libraries: new[] { new LibraryFolder("Shows", new[] { "/media/tv" }), new LibraryFolder("Linked", new[] { "/media/links" }) });

        Assert.Equal(7, build.Counts.Unmapped);
        Assert.Equal(8, build.Counts.Gaps);
        Assert.Single(build.Entries);
        var finding = Assert.Single(build.Findings, f => f.Code == "MostlyUnmapped");
        Assert.Contains("7 of 8", finding.Message, StringComparison.Ordinal);
        Assert.Contains("/media/links (library Linked)", finding.Message, StringComparison.Ordinal);
        Assert.Equal(5, finding.Examples!.Count);
    }

    [Fact]
    public void Unmapped_GapAnotherEncoderMaps_IsNotCountedHere()
    {
        // One encoder per library: the films a capped viewer watches are the film encoder's gaps,
        // not unmapped ones for the show encoder.
        var shows = new EncodeTarget { Id = "t1", Name = "Shows", Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = "/media/tv" } } };
        var films = new EncodeTarget { Id = "t2", Name = "Films", Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = "/media/films" } } };
        var options = Options(shows, films);
        var demand = new Demand();
        for (var i = 0; i < 7; i++)
        {
            demand.Ask(demand.Item($"/media/films/Film {i}.mkv", 1080), V720);
        }

        demand.Ask(demand.Item("/media/tv/Show A/x.mkv", 1080), V720);
        var libraries = new[] { new LibraryFolder("Shows", new[] { "/media/tv" }), new LibraryFolder("Films", new[] { "/media/films" }) };

        var showBuild = Build(options.Targets[0], demand, options, libraries);
        var filmBuild = Build(options.Targets[1], demand, options, libraries);

        Assert.Equal((1, 1, 0), (showBuild.Counts.Gaps, showBuild.Counts.Listed, showBuild.Counts.Unmapped));
        Assert.Equal((7, 7, 0), (filmBuild.Counts.Gaps, filmBuild.Counts.Listed, filmBuild.Counts.Unmapped));
        Assert.DoesNotContain(showBuild.Findings, f => f.Code == "MostlyUnmapped");
        Assert.DoesNotContain(filmBuild.Findings, f => f.Code == "MostlyUnmapped");
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("dryrun")]
    [InlineData("taller")]
    public void Unmapped_GapOnlyAnEncoderThatWritesNothingOrCannotHelpMaps_StillCounts(string other)
    {
        // A disabled or dry-run encoder makes no copy, and a 1080p one cannot help a 720p viewer,
        // so the gap is still nobody's.
        var shows = new EncodeTarget { Id = "t1", Name = "Shows", Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = "/media/tv" } } };
        var films = new EncodeTarget
        {
            Id = "t2",
            Name = "Films",
            Enabled = other != "disabled",
            DryRun = other == "dryrun",
            OutputHeight = other == "taller" ? 1080 : 720,
            Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = "/media/films" } },
        };
        var options = Options(shows, films);
        var demand = new Demand();
        for (var i = 0; i < 7; i++)
        {
            demand.Ask(demand.Item($"/media/films/Film {i}.mkv", 2160), V720);
        }

        demand.Ask(demand.Item("/media/tv/Show A/x.mkv", 1080), V720);

        var build = Build(
            options.Targets[0],
            demand,
            options,
            new[] { new LibraryFolder("Shows", new[] { "/media/tv" }), new LibraryFolder("Films", new[] { "/media/films" }) });

        Assert.Equal((8, 7), (build.Counts.Gaps, build.Counts.Unmapped));
        var finding = Assert.Single(build.Findings, f => f.Code == "MostlyUnmapped");
        Assert.Contains("/media/films (library Films)", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveSymlinks_MapsTheTargetAndCountsFailures()
    {
        var demand = new Demand();
        demand.Ask(demand.Item("/media/links/Show A/x.mkv", 1080), V720);
        demand.Ask(demand.Item("/media/links/Show B/x.mkv", 1080), V720);

        var build = Build(
            Target(t => t.ResolveSymlinks = true),
            demand,
            resolveLink: p => p.Contains("Show A", StringComparison.Ordinal) ? p.Replace("/media/links", "/media/tv", StringComparison.Ordinal) : null);

        Assert.Equal(new[] { "Show A/x.mkv" }, build.Paths);
        Assert.Equal(1, build.Counts.SymlinkFailures);
        Assert.Equal(1, build.Counts.Unmapped);
    }

    // --- order, dedupe, cut ---

    [Fact]
    public void Order_IsTierThenDepthThenViewersThenRecencyThenPath()
    {
        var demand = new Demand();
        var favourite = demand.Item("/media/tv/F/x.mkv", 1080);
        var deep = demand.Item("/media/tv/D/x.mkv", 1080);
        var nextOld = demand.Item("/media/tv/C/x.mkv", 1080);
        var nextRecent = demand.Item("/media/tv/B/x.mkv", 1080);
        var nextTwoViewers = demand.Item("/media/tv/Z/x.mkv", 1080);
        var resumeB = demand.Item("/media/tv/R2/x.mkv", 1080);
        var resumeA = demand.Item("/media/tv/R1/x.mkv", 1080);
        var playing = demand.Item("/media/tv/P/x.mkv", 1080);
        demand
            .Ask(favourite, V720, DemandTier.Favourite, 0)
            .Ask(deep, V720, DemandTier.NextUp, 2)
            .Ask(deep, V720b, DemandTier.NextUp, 2)
            .Ask(nextOld, V720, DemandTier.NextUp, 1, Now.AddDays(-3))
            .Ask(nextRecent, V720, DemandTier.NextUp, 1, Now.AddDays(-1))
            .Ask(nextTwoViewers, V720, DemandTier.NextUp, 1, Now.AddDays(-9))
            .Ask(nextTwoViewers, V720b, DemandTier.NextUp, 1, Now.AddDays(-9))
            .Ask(resumeB, V720, DemandTier.ContinueWatching, 0)
            .Ask(resumeA, V720, DemandTier.ContinueWatching, 0)
            .Ask(playing, V720b, DemandTier.NowPlaying, 0)
            .Ask(playing, V720, DemandTier.Favourite, 0);

        var build = Build(Target(), demand);

        Assert.Equal(
            new[] { "P/x.mkv", "R1/x.mkv", "R2/x.mkv", "Z/x.mkv", "B/x.mkv", "C/x.mkv", "D/x.mkv", "F/x.mkv" },
            build.Paths);
        Assert.Equal(new[] { "playing", "favourite" }, build.Entries[0].Reasons);
        Assert.Equal(new[] { "next up +1" }, build.Entries[6].Reasons);
    }

    [Fact]
    public void Order_AndReasons_ComeOnlyFromViewersForWhomTheItemIsAGap()
    {
        // The 1080p viewer playing M already has a 1080p version, so M is a gap only for the
        // 720p viewer who favourited it. It must not rank as "playing" above N, that viewer's
        // own continue-watching gap.
        var demand = new Demand();
        var m = demand.Item("/media/tv/M/m.mkv", 2160, 1080);
        var n = demand.Item("/media/tv/N/n.mkv", 2160);
        demand.Ask(m, V1080, DemandTier.NowPlaying, 0)
            .Ask(m, V720, DemandTier.Favourite, 1)
            .Ask(n, V720, DemandTier.ContinueWatching, 0);

        var build = Build(Target(), demand);

        Assert.Equal(n, build.Entries[0].ItemId);
        var forM = build.Entries.Where(e => e.ItemId == m).ToList();
        Assert.NotEmpty(forM);
        Assert.All(forM, e => Assert.Equal((DemandTier.Favourite, 1, 1), (e.Tier, e.Depth, e.Users)));
        Assert.All(forM, e => Assert.Equal(new[] { DemandTier.Reason(DemandTier.Favourite) }, e.Reasons));
    }

    [Fact]
    public void Dedupe_KeepsTheFirstPosition_AndTheCutIsCounted()
    {
        var demand = new Demand();
        var first = demand.Item("/media/tv/A/x.mkv", 1080);
        var sameFile = Guid.NewGuid();
        demand.Collected.Versions[sameFile] = new[] { new VersionInfo(sameFile, "/media/tv/A/x.mkv", 1080) };
        demand.Ask(first, V720, DemandTier.NowPlaying, 0).Ask(sameFile, V720, DemandTier.Favourite, 0);
        for (var i = 0; i < 5; i++)
        {
            demand.Ask(demand.Item($"/media/tv/N{i}/x.mkv", 1080), V720);
        }

        var build = Build(Target(t => t.MaxEntries = 3), demand);

        Assert.Equal(new[] { "A/x.mkv", "N0/x.mkv", "N1/x.mkv" }, build.Paths);
        Assert.Equal(3, build.Counts.CutByLimit);
        Assert.Equal(3, build.Counts.Listed);
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        var demand = new Demand();
        for (var i = 0; i < 20; i++)
        {
            demand.Ask(demand.Item($"/media/tv/S{i % 7}/E{i}.mkv", 2160), i % 2 == 0 ? V720 : V1080, DemandTier.NextUp, 1 + (i % 3));
        }

        var first = Build(Target(), demand).Paths;
        var second = Build(Target(), demand).Paths;

        Assert.Equal(first, second);
        Assert.Equal(20, first.Count);
    }

    [Fact]
    public void AudienceUnion_IsEveryViewerAnyRunnableTargetCounts()
    {
        var options = Options(
            new EncodeTarget { Id = "a", OutputHeight = 480, Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = "/media/tv" } } },
            new EncodeTarget { Id = "b", Enabled = false, AudienceMode = "Users", AudienceUserIds = new List<Guid> { Uncapped.Id }, Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = "/media/tv" } } });

        var union = PriorityListBuilder.AudienceUnion(options, Everyone, Now);

        Assert.Equal(new[] { V480.Id, V720.Id, V720b.Id, V1080.Id }.OrderBy(x => x), union.OrderBy(x => x));
    }

    [Fact]
    public void FolderFindings_FlagAMissingFolderAndOneOutsideEveryLibrary()
    {
        var target = Target(t => t.Folders = new List<EncodeFolderMapping>
        {
            new() { JellyfinPath = "/media/tv" },
            new() { JellyfinPath = "/mnt/host/tv" },
            new() { JellyfinPath = "/srv/other" },
        });

        var build = PriorityListBuilder.Build(target, new BuildInput
        {
            Options = Options(),
            Viewers = Everyone,
            Demand = new CollectedDemand(),
            Libraries = new[] { new LibraryFolder("Shows", new[] { "/media/tv" }) },
            Policies = Array.Empty<PolicySummary>(),
            NowUtc = Now,
            DirectoryExists = p => p != "/mnt/host/tv",
        });

        Assert.Equal("/mnt/host/tv", Assert.Single(build.Findings, f => f.Code == "FolderMissing").Message.Split(' ')[0]);
        Assert.StartsWith("/srv/other", Assert.Single(build.Findings, f => f.Code == "FolderNotInLibrary").Message, StringComparison.Ordinal);
    }
}
