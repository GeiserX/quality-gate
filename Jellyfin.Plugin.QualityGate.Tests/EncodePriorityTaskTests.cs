using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.EncodePriority;
using Jellyfin.Plugin.QualityGate.Tests.Harness;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.QualityGate.Tests;

/// <summary>
/// The scheduled task end to end on a real temporary folder, with the demand queries faked.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class EncodePriorityTaskTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _root;
    private readonly string _tv;
    private readonly string _movies;
    private readonly string _data;
    private readonly Plugin _plugin;
    private readonly FakeCollector _collector = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IActivityManager> _activity = new();
    private readonly List<ActivityLog> _entries = new();
    private readonly User _viewer;
    private DateTime _now = Now;

    public EncodePriorityTaskTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-task-" + Guid.NewGuid().ToString("N")[..8]);
        _tv = Path.Combine(_root, "media", "tv");
        _movies = Path.Combine(_root, "media", "movies");
        _data = Path.Combine(_root, "data");
        Directory.CreateDirectory(_tv);
        Directory.CreateDirectory(_movies);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.SetReturnsDefault<string>(_root);
        appPaths.Setup(p => p.DataPath).Returns(_data);
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>())).Returns(new PluginConfiguration());
        _plugin = new Plugin(appPaths.Object, xml.Object);

        _viewer = new User("viewer", "provider", "reset") { LastActivityDate = Now.AddHours(-1) };
        _users.Setup(u => u.GetUsers()).Returns(() => new[] { _viewer });
        _library.Setup(l => l.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new() { Name = "Shows", Locations = new[] { _tv } },
            new() { Name = "Films", Locations = new[] { _movies } },
        });
        _activity.Setup(a => a.CreateAsync(It.IsAny<ActivityLog>())).Callback<ActivityLog>(_entries.Add).Returns(System.Threading.Tasks.Task.CompletedTask);

        var config = _plugin.Configuration;
        config.Policies = new List<QualityPolicy> { new() { Id = "p720", Name = "HD", MaxHeight = 720 } };
        config.DefaultPolicyId = "p720";
        config.EnableEncodePriority = true;
        config.EncodeTargets = new List<EncodeTarget>
        {
            new() { Id = "aaaaaaaa-shows", Name = "Shows", Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = _tv } } },
        };
    }

    public void Dispose()
    {
        foreach (var dir in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).Prepend(_root))
        {
            SetMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Directory.Delete(_root, true);
    }

    private string ShowsFile => Path.Combine(_tv, PriorityFileWriter.SourceFolderFileName);

    private EncodePriorityTask NewTask() => new(
        _collector,
        _users.Object,
        _library.Object,
        _activity.Object,
        new Mock<IApplicationPaths>().Also(p => p.Setup(x => x.DataPath).Returns(_data)).Object,
        Mock.Of<ILogger<EncodePriorityTask>>(),
        () => _now);

    private Guid Wants(string path, int? height, int tier = DemandTier.NextUp, int depth = 1)
    {
        var id = Guid.NewGuid();
        _collector.Demand.Signals.Add(new DemandSignal(id, _viewer.Id, tier, depth, Now));
        _collector.Demand.Versions[id] = new[] { new VersionInfo(id, path, height) };
        return id;
    }

    private EncodePriorityState State() => EncodePriorityState.Load(EncodePriorityPaths.StateFile(_data), out _);

    [Fact]
    public void Task_IsFoundByKeyAndRunsHourlyAndAtStartup()
    {
        var task = NewTask();

        Assert.Equal("QualityGateEncodePriority", task.Key);
        Assert.Equal("Quality Gate", task.Category);
        Assert.Equal(
            new[] { TaskTriggerInfoType.IntervalTrigger, TaskTriggerInfoType.StartupTrigger },
            task.GetDefaultTriggers().Select(t => t.Type));
        Assert.Equal(TimeSpan.FromHours(1).Ticks, task.GetDefaultTriggers().First().IntervalTicks);
    }

    [Fact]
    public async Task Run_WritesTheListBesideTheMediaAndRecordsIt()
    {
        Wants(Path.Combine(_tv, "Show A", "Season 1", "Show A S01E02.mkv"), 1080);
        Wants(Path.Combine(_tv, "Show A", "Season 1", "Show A S01E01.mkv"), 1080, DemandTier.ContinueWatching, 0);
        Wants(Path.Combine(_tv, "Show B", "Season 1", "Show B S01E01.mkv"), 720);

        await NewTask().RunAsync("test", null, CancellationToken.None);

        Assert.Equal(
            new[] { "Show A/Season 1/Show A S01E01.mkv", "Show A/Season 1/Show A S01E02.mkv" },
            PriorityFileWriter.Read(ShowsFile).Paths);
        var state = State();
        Assert.Equal(new[] { ShowsFile }, state.WrittenFiles);
        var target = state.Targets["aaaaaaaa-shows"];
        Assert.Equal("OK", target.Result);
        Assert.Equal("test", target.Trigger);
        Assert.Equal(2, target.Counts.Listed);
        Assert.Equal(1, target.Counts.Viewers);
        Assert.Single(_entries);
        Assert.Equal(EncodePriorityTask.ActivityType, _entries[0].Type);
        Assert.Equal(Guid.Empty, _entries[0].UserId);
        Assert.StartsWith("Shows: 2 listed (+2, -0) · 1 viewers · 0 unmapped", _entries[0].ShortOverview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FeatureOff_RunsOnlyTheCleanupPass()
    {
        Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        Assert.True(File.Exists(ShowsFile));
        _collector.Calls = 0;

        _plugin.Configuration.EnableEncodePriority = false;
        await NewTask().RunAsync("test", null, CancellationToken.None);

        Assert.False(File.Exists(ShowsFile));
        Assert.Equal(0, _collector.Calls);
        _users.Verify(u => u.GetUsers(), Times.Once);
        Assert.Empty(State().WrittenFiles);
        Assert.Empty(State().Targets);
    }

    [Fact]
    public async Task FeatureNeverOn_CreatesNoFolderAndReadsNoLibrary()
    {
        _plugin.Configuration.EnableEncodePriority = false;

        await NewTask().RunAsync("scheduled", null, CancellationToken.None);
        await NewTask().RunAsync("settings saved", null, CancellationToken.None);

        Assert.False(Directory.Exists(EncodePriorityPaths.Root(_data)));
        _library.Verify(l => l.GetVirtualFolders(), Times.Never);
        Assert.Equal(0, _collector.Calls);
    }

    [Fact]
    public async Task FeatureOff_AfterTheCleanup_LeavesTheStateFileAlone()
    {
        Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        _plugin.Configuration.EnableEncodePriority = false;
        await NewTask().RunAsync("test", null, CancellationToken.None);
        var statePath = EncodePriorityPaths.StateFile(_data);
        var stamp = File.GetLastWriteTimeUtc(statePath);
        File.SetLastWriteTimeUtc(statePath, stamp.AddHours(-1));

        _now = Now.AddHours(1);
        await NewTask().RunAsync("scheduled", null, CancellationToken.None);

        Assert.Equal(stamp.AddHours(-1), File.GetLastWriteTimeUtc(statePath));
    }

    [Fact]
    public async Task TargetDisabledOrSwitchedToDryRun_HasItsFileRemoved()
    {
        Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);
        await NewTask().RunAsync("test", null, CancellationToken.None);

        _plugin.Configuration.EncodeTargets[0].DryRun = true;
        await NewTask().RunAsync("test", null, CancellationToken.None);

        Assert.False(File.Exists(ShowsFile));
        Assert.Equal("DryRun", State().Targets["aaaaaaaa-shows"].Result);
        Assert.Equal(1, State().Targets["aaaaaaaa-shows"].Counts.Listed);
    }

    [Fact]
    public async Task BudgetExceeded_WritesNothingAndKeepsThePreviousList()
    {
        Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        var before = File.ReadAllText(ShowsFile);

        Wants(Path.Combine(_tv, "Show B", "x.mkv"), 1080);
        _collector.BlockUntilCancelled = true;
        var task = NewTask();
        task.BudgetOverride = TimeSpan.FromMilliseconds(50);
        _now = Now.AddHours(1);
        await task.RunAsync("test", null, CancellationToken.None);

        Assert.Equal(before, File.ReadAllText(ShowsFile));
        var target = State().Targets["aaaaaaaa-shows"];
        Assert.Equal("TimedOut", target.Result);
        Assert.Equal("TimedOut", Assert.Single(target.Findings).Code);
        Assert.Single(target.Entries);
    }

    [Fact]
    public async Task TheTasksOwnCancellation_IsNotSwallowed()
    {
        _collector.BlockUntilCancelled = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewTask().RunAsync("test", null, cts.Token));
        Assert.False(File.Exists(ShowsFile));
    }

    [Fact]
    public async Task AnExceptionInOneTarget_DoesNotStopTheOthers()
    {
        var broken = Path.Combine(_root, "media", "broken");
        _plugin.Configuration.EncodeTargets.Insert(0, new EncodeTarget
        {
            Id = "bbbbbbbb-broken",
            Name = "Broken",
            Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = broken } },
        });
        Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);
        var task = NewTask();
        task.DirectoryExists = p => p == broken ? throw new InvalidOperationException("boom") : Directory.Exists(p);

        await task.RunAsync("test", null, CancellationToken.None);

        var state = State();
        Assert.Equal("Error", state.Targets["bbbbbbbb-broken"].Result);
        Assert.Equal("boom", state.Targets["bbbbbbbb-broken"].Error);
        Assert.Equal("OK", state.Targets["aaaaaaaa-shows"].Result);
        Assert.Equal(new[] { "Show A/x.mkv" }, PriorityFileWriter.Read(ShowsFile).Paths);
    }

    [Fact]
    public async Task ActivityEntries_AreWrittenOnlyWhenTheListOrItsFindingsChange()
    {
        Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);

        await NewTask().RunAsync("test", null, CancellationToken.None);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        Assert.Single(_entries);

        Wants(Path.Combine(_tv, "Show B", "x.mkv"), 1080);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        Assert.Equal(2, _entries.Count);
        Assert.Contains("(+1, -0)", _entries[1].ShortOverview, StringComparison.Ordinal);

        Wants(Path.Combine(_root, "elsewhere", "x.mkv"), null);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        Assert.Equal(3, _entries.Count);
        Assert.Contains("UnknownHeights", _entries[2].Overview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadOnlyMediaFolder_IsAWriteFailedFindingAndOtherTargetsStillWrite()
    {
        if (!ReadOnlyFolders.AreEnforced)
        {
            return;
        }

        _plugin.Configuration.EncodeTargets.Add(new EncodeTarget
        {
            Id = "cccccccc-films",
            Name = "Films",
            OutputMode = "DataFolder",
            Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = _movies } },
        });
        Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);
        Wants(Path.Combine(_movies, "Film A (2001)", "Film A (2001).mkv"), 2160);
        SetMode(_tv, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        await NewTask().RunAsync("test", null, CancellationToken.None);

        var state = State();
        var shows = state.Targets["aaaaaaaa-shows"];
        Assert.Equal("Warning", shows.Result);
        Assert.Contains("Data folder", Assert.Single(shows.Findings, f => f.Code == "WriteFailed").Message, StringComparison.Ordinal);
        var filmsFile = EncodePriorityPaths.DataFolderFile(_data, "cccccccc-films");
        Assert.Equal(new[] { "Film A (2001)/Film A (2001).mkv" }, PriorityFileWriter.Read(filmsFile).Paths);
        Assert.Equal(new[] { filmsFile }, state.WrittenFiles);
    }

    [Fact]
    public async Task TwoTargetsOnOneFile_OnlyTheFirstWrites()
    {
        _plugin.Configuration.EncodeTargets.Add(new EncodeTarget
        {
            Id = "dddddddd-copy",
            Name = "Copy",
            OutputHeight = 480,
            Folders = new List<EncodeFolderMapping> { new() { JellyfinPath = _tv } },
        });
        Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);

        await NewTask().RunAsync("test", null, CancellationToken.None);

        Assert.Equal("Shows", JsonTarget(ShowsFile));
        Assert.Equal("OutputConflict", Assert.Single(State().Targets["dddddddd-copy"].Findings).Code);
    }

    [Fact]
    public async Task AListedItemThatGainsAWithinCapVersion_IsRecordedAsCovered()
    {
        var item = Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);
        await NewTask().RunAsync("test", null, CancellationToken.None);

        var copy = Guid.NewGuid();
        _collector.Demand.Versions[item] = new[]
        {
            new VersionInfo(item, Path.Combine(_tv, "Show A", "x.mkv"), 1080),
            new VersionInfo(copy, Path.Combine(_tv, "Show A", "x - 720p.mkv"), 720),
        };
        _now = Now.AddHours(3);
        await NewTask().RunAsync("test", null, CancellationToken.None);

        var state = State();
        var covered = Assert.Single(state.Covered);
        Assert.Equal((item, copy, Now, Now.AddHours(3)), (covered.ItemId, covered.CoveredVersionId, covered.ListedAt, covered.CoveredAt));
        Assert.Equal(1, state.Targets["aaaaaaaa-shows"].Counts.Covered);
        Assert.Empty(PriorityFileWriter.Read(ShowsFile).Paths);
    }

    [Fact]
    public async Task ACopyMeasuredOneRunLate_IsStillRecordedAsCovered()
    {
        // A run between the copy appearing and its probe finishing sees no height. The item
        // leaves the list (an unknown height counts as within the cap) and must be checked
        // again once the height is known.
        var original = Path.Combine(_tv, "Show A", "x.mkv");
        var item = Wants(original, 1080);
        await NewTask().RunAsync("test", null, CancellationToken.None);

        var copy = Guid.NewGuid();
        var copyPath = Path.Combine(_tv, "Show A", "x - 720p.mkv");
        _collector.Demand.Versions[item] = new[] { new VersionInfo(item, original, 1080), new VersionInfo(copy, copyPath, null) };
        _now = Now.AddHours(1);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        Assert.Empty(State().Covered);
        Assert.Equal(item, Assert.Single(State().Targets["aaaaaaaa-shows"].PendingCover).ItemId);

        _collector.Demand.Versions[item] = new[] { new VersionInfo(item, original, 1080), new VersionInfo(copy, copyPath, 720) };
        _now = Now.AddHours(2);
        await NewTask().RunAsync("test", null, CancellationToken.None);

        var covered = Assert.Single(State().Covered);
        Assert.Equal((item, copy, Now, Now.AddHours(2)), (covered.ItemId, covered.CoveredVersionId, covered.ListedAt, covered.CoveredAt));
        Assert.Empty(State().Targets["aaaaaaaa-shows"].PendingCover);
    }

    [Fact]
    public async Task APendingItemWhoseHeightNeverArrives_IsDroppedAfterAWeek()
    {
        var original = Path.Combine(_tv, "Show A", "x.mkv");
        var item = Wants(original, 1080);
        await NewTask().RunAsync("test", null, CancellationToken.None);

        _collector.Demand.Versions[item] = new[] { new VersionInfo(item, original, 1080), new VersionInfo(Guid.NewGuid(), Path.Combine(_tv, "Show A", "x - 720p.mkv"), null) };
        _now = Now.AddHours(1);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        _now = Now.AddDays(6);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        Assert.Single(State().Targets["aaaaaaaa-shows"].PendingCover);

        _now = Now.AddDays(8);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        Assert.Empty(State().Targets["aaaaaaaa-shows"].PendingCover);
        Assert.Empty(State().Covered);
    }

    [Fact]
    public async Task AnUnchangedListWithNoCoverForTwoDays_IsFlaggedStuck()
    {
        Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);
        await NewTask().RunAsync("test", null, CancellationToken.None);

        _now = Now.AddHours(47);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        Assert.DoesNotContain(State().Targets["aaaaaaaa-shows"].Findings, f => f.Code == "Stuck");

        _now = Now.AddHours(49);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        Assert.Contains(State().Targets["aaaaaaaa-shows"].Findings, f => f.Code == "Stuck");
    }

    [Fact]
    public async Task Preview_BuildsAsADryRunAndLeavesTheRealListAlone()
    {
        EncodePriorityRuntime.Reset();
        Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        var before = File.ReadAllText(ShowsFile);
        var realRun = State().Targets["aaaaaaaa-shows"];
        _entries.Clear();

        Wants(Path.Combine(_tv, "Show B", "x.mkv"), 1080, DemandTier.ContinueWatching, 0);
        _now = Now.AddHours(1);
        EncodePriorityRuntime.RequestPreview();
        await NewTask().ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(before, File.ReadAllText(ShowsFile));
        Assert.Empty(_entries);
        var state = State();
        Assert.Equal(realRun.LastRunUtc, state.Targets["aaaaaaaa-shows"].LastRunUtc);
        Assert.Equal(new[] { "Show A/x.mkv" }, state.Targets["aaaaaaaa-shows"].Entries.Select(e => e.Path));
        var preview = Assert.IsType<PreviewState>(state.Preview);
        Assert.Equal("OK", preview.Result);
        Assert.Equal(Now.AddHours(1), preview.RanAtUtc);
        var target = preview.Targets["aaaaaaaa-shows"];
        Assert.Equal("DryRun", target.Result);
        Assert.Equal("preview", target.Trigger);
        Assert.Equal(ShowsFile, target.OutputPath);
        Assert.Equal(new[] { "Show B/x.mkv", "Show A/x.mkv" }, target.Entries.Select(e => e.Path));
        Assert.Equal(Now, target.Entries[1].FirstListedAt);
    }

    [Fact]
    public async Task Preview_WithTheFeatureOff_BuildsAndRemovesNothing()
    {
        EncodePriorityRuntime.Reset();
        Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        _plugin.Configuration.EnableEncodePriority = false;

        EncodePriorityRuntime.RequestPreview();
        await NewTask().ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.True(File.Exists(ShowsFile));
        Assert.Equal(new[] { ShowsFile }, State().WrittenFiles);
        Assert.Equal(1, State().Preview!.Targets["aaaaaaaa-shows"].Counts.Listed);
    }

    [Fact]
    public async Task Preview_OnATargetWithNoFile_WritesNone()
    {
        EncodePriorityRuntime.Reset();
        Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);

        EncodePriorityRuntime.RequestPreview();
        await NewTask().ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.False(File.Exists(ShowsFile));
        Assert.Empty(Directory.GetFiles(_tv, "*", SearchOption.AllDirectories));
        Assert.Empty(State().WrittenFiles);
        Assert.Empty(State().Targets);
        Assert.Equal(1, State().Preview!.Targets["aaaaaaaa-shows"].Counts.Listed);
    }

    [Fact]
    public async Task Preview_ThatTimesOut_SaysSoAndWritesNothing()
    {
        EncodePriorityRuntime.Reset();
        Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);
        _collector.BlockUntilCancelled = true;
        var task = NewTask();
        task.BudgetOverride = TimeSpan.FromMilliseconds(50);

        EncodePriorityRuntime.RequestPreview();
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.False(File.Exists(ShowsFile));
        Assert.Equal("TimedOut", State().Preview!.Result);
        Assert.Equal("TimedOut", Assert.Single(State().Preview!.Targets["aaaaaaaa-shows"].Findings).Code);
    }

    [Fact]
    public async Task Preview_QueuedWithAnotherTrigger_AlsoRunsForReal()
    {
        EncodePriorityRuntime.Reset();
        Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);

        EncodePriorityRuntime.RequestRun("playback");
        EncodePriorityRuntime.RequestPreview();
        await NewTask().ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.True(File.Exists(ShowsFile));
        Assert.Equal("playback", State().Targets["aaaaaaaa-shows"].Trigger);
        Assert.NotNull(State().Preview);
        Assert.False(EncodePriorityRuntime.TakePreview());
    }

    [Fact]
    public async Task Preview_ThatIsCancelled_KeepsTheOtherTriggersForTheNextRun()
    {
        EncodePriorityRuntime.Reset();
        Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);
        _collector.BlockUntilCancelled = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        EncodePriorityRuntime.RequestRun("playback");
        EncodePriorityRuntime.RequestRun("settings saved");
        EncodePriorityRuntime.RequestPreview();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewTask().ExecuteAsync(new Progress<double>(), cts.Token));

        Assert.False(File.Exists(ShowsFile));
        Assert.Equal(("playback, settings saved", false), EncodePriorityRuntime.TakeRequest());
    }

    private async Task<(Guid Item, Guid Copy)> CoverAnItem()
    {
        EncodePriorityRuntime.Reset();
        var item = Wants(Path.Combine(_tv, "Show A", "x.mkv"), 1080);
        await NewTask().RunAsync("test", null, CancellationToken.None);

        var copy = Guid.NewGuid();
        _collector.Demand.Versions[item] = new[]
        {
            new VersionInfo(item, Path.Combine(_tv, "Show A", "x.mkv"), 1080),
            new VersionInfo(copy, Path.Combine(_tv, "Show A", "x - 720p.mkv"), 720),
        };
        _now = Now.AddHours(3);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        Assert.Single(State().Covered);
        Assert.True(EncodePriorityRuntime.IsCovered(item));
        return (item, copy);
    }

    [Fact]
    public async Task ACappedPlayOnTheWithinCapVersion_RecordsServedAt()
    {
        var (item, copy) = await CoverAnItem();

        EncodePriorityRuntime.RecordServed(new ServedPlay(item, copy, 720, Now.AddHours(4)));
        _now = Now.AddHours(5);
        await NewTask().RunAsync("test", null, CancellationToken.None);

        var covered = Assert.Single(State().Covered);
        Assert.Equal(Now.AddHours(4), covered.ServedAt);
        Assert.Equal(copy, covered.ServedVersionId);
        Assert.Null(covered.ServedOverCapAt);
        Assert.DoesNotContain(State().Targets["aaaaaaaa-shows"].Findings, f => f.Code == "ServedOverCap");
        Assert.Empty(EncodePriorityRuntime.DrainServed());
    }

    [Fact]
    public async Task ACappedPlayOnTheOverCapVersion_RaisesServedOverCap()
    {
        var (item, _) = await CoverAnItem();

        EncodePriorityRuntime.RecordServed(new ServedPlay(item, item, 720, Now.AddHours(4)));
        _now = Now.AddHours(5);
        await NewTask().RunAsync("test", null, CancellationToken.None);

        var covered = Assert.Single(State().Covered);
        Assert.Null(covered.ServedAt);
        Assert.Equal(Now.AddHours(4), covered.ServedOverCapAt);
        Assert.Equal(item, covered.ServedOverCapVersionId);
        var finding = Assert.Single(State().Targets["aaaaaaaa-shows"].Findings, f => f.Code == "ServedOverCap");
        Assert.Equal(new[] { item.ToString("N") }, finding.Examples);
        Assert.Contains(_entries, e => e.Overview?.Contains("ServedOverCap", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task AViewerCappedBelowEveryVersion_IsACorrectTranscode_NotServedOverCap()
    {
        // Every capped viewer's play of a covered item is noted, not only the viewers the copy
        // was made for. With no version within 480, a transcode of the 720p copy is correct.
        var (item, copy) = await CoverAnItem();

        EncodePriorityRuntime.RecordServed(new ServedPlay(item, copy, 480, Now.AddHours(4)));
        EncodePriorityRuntime.RecordServed(new ServedPlay(item, item, 480, Now.AddHours(4)));
        _now = Now.AddHours(5);
        await NewTask().RunAsync("test", null, CancellationToken.None);

        var covered = Assert.Single(State().Covered);
        Assert.Null(covered.ServedOverCapAt);
        Assert.Null(covered.ServedAt);
        Assert.DoesNotContain(State().Targets["aaaaaaaa-shows"].Findings, f => f.Code == "ServedOverCap");
    }

    [Fact]
    public async Task AWithinCapPlayOfAnotherVersion_DoesNotCountAsServed()
    {
        // A viewer capped at 1080 playing the 1080p original never needed the 720p copy, so
        // the play proves nothing about it.
        var (item, copy) = await CoverAnItem();

        EncodePriorityRuntime.RecordServed(new ServedPlay(item, item, 1080, Now.AddHours(4)));
        _now = Now.AddHours(5);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        Assert.Null(Assert.Single(State().Covered).ServedAt);

        EncodePriorityRuntime.RecordServed(new ServedPlay(item, copy, 720, Now.AddHours(6)));
        _now = Now.AddHours(7);
        await NewTask().RunAsync("test", null, CancellationToken.None);
        var covered = Assert.Single(State().Covered);
        Assert.Equal(Now.AddHours(6), covered.ServedAt);
        Assert.Equal(copy, covered.ServedVersionId);
    }

    [Fact]
    public async Task APlayThatCannotProveAnything_IsDropped()
    {
        var (item, copy) = await CoverAnItem();

        EncodePriorityRuntime.RecordServed(new ServedPlay(item, null, 720, Now.AddHours(4)));
        EncodePriorityRuntime.RecordServed(new ServedPlay(item, copy, 720, Now.AddHours(2)));
        EncodePriorityRuntime.RecordServed(new ServedPlay(item, Guid.NewGuid(), 720, Now.AddHours(4)));
        _now = Now.AddHours(5);
        await NewTask().RunAsync("test", null, CancellationToken.None);

        var covered = Assert.Single(State().Covered);
        Assert.Null(covered.ServedAt);
        Assert.Null(covered.ServedOverCapAt);
        Assert.Empty(EncodePriorityRuntime.DrainServed());
    }

    [Fact]
    public async Task TheServedQueue_SurvivesACancelledRunAndIsDrainedByTheNextOne()
    {
        var (item, copy) = await CoverAnItem();
        EncodePriorityRuntime.RecordServed(new ServedPlay(item, copy, 720, Now.AddHours(4)));

        _collector.BlockUntilCancelled = true;
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewTask().RunAsync("test", null, cts.Token));
        }

        Assert.Null(Assert.Single(State().Covered).ServedAt);

        _collector.BlockUntilCancelled = false;
        _now = Now.AddHours(5);
        await NewTask().RunAsync("test", null, CancellationToken.None);

        Assert.Equal(Now.AddHours(4), Assert.Single(State().Covered).ServedAt);
        Assert.Empty(EncodePriorityRuntime.DrainServed());
    }

    [Fact]
    public async Task APreview_LeavesTheServedQueueForTheRealRun()
    {
        var (item, copy) = await CoverAnItem();
        EncodePriorityRuntime.RecordServed(new ServedPlay(item, copy, 720, Now.AddHours(4)));

        await NewTask().PreviewAsync(null, CancellationToken.None);

        Assert.Single(EncodePriorityRuntime.DrainServed());
    }

    private static void SetMode(string dir, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dir, mode);
        }
    }

    private static string? JsonTarget(string file)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(file));
        return doc.RootElement.GetProperty("target").GetString();
    }

    private sealed class FakeCollector : ICandidateCollector
    {
        public CollectedDemand Demand { get; } = new();

        public int Calls { get; set; }

        public bool BlockUntilCancelled { get; set; }

        public CollectedDemand Collect(EncodePriorityOptions options, IReadOnlyList<User> users, DateTime nowUtc, CancellationToken cancellationToken, CollectedDemand? into = null)
        {
            Calls++;
            var demand = into ?? new CollectedDemand();
            if (BlockUntilCancelled)
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                cancellationToken.ThrowIfCancellationRequested();
            }

            demand.UsersProcessed = users.Count;
            demand.Signals.AddRange(Demand.Signals);
            foreach (var (id, versions) in Demand.Versions)
            {
                demand.Versions[id] = versions;
            }

            return demand;
        }

        public IReadOnlyList<VersionInfo> GetVersions(Guid itemId) => Demand.Versions.GetValueOrDefault(itemId) ?? Array.Empty<VersionInfo>();
    }
}

internal static class MockExtensions
{
    public static Mock<T> Also<T>(this Mock<T> mock, Action<Mock<T>> setup)
        where T : class
    {
        setup(mock);
        return mock;
    }
}
