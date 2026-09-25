using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.EncodePriority;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.QualityGate.Tests;

/// <summary>
/// What queues a priority run, and what does not: playback start with a debounce, settings
/// saved, a library scan, and uninstall's cleanup.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class EncodePriorityTriggerTests : IDisposable
{
    private readonly string _dir;
    private readonly Plugin _plugin;
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly Mock<ITaskManager> _tasks = new();
    private readonly ManualTime _time = new();
    private readonly User _capped = new("capped", "provider", "reset");
    private readonly User _uncapped = new("uncapped", "provider", "reset");
    private readonly EncodePriorityEvents _events;

    public EncodePriorityTriggerTests()
    {
        EncodePriorityRuntime.Reset();
        _dir = Path.Combine(Path.GetTempPath(), "qg-trig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.SetReturnsDefault<string>(_dir);
        appPaths.Setup(p => p.DataPath).Returns(Path.Combine(_dir, "data"));
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>())).Returns(new PluginConfiguration());
        _plugin = new Plugin(appPaths.Object, xml.Object);

        var config = _plugin.Configuration;
        config.Policies = new List<QualityPolicy> { new() { Id = "p720", Name = "HD", MaxHeight = 720 } };
        config.UserPolicies = new List<UserPolicyAssignment>
        {
            new() { UserId = _capped.Id, PolicyId = "p720" },
            new() { UserId = _uncapped.Id, PolicyId = UserPolicyAssignment.FullAccessPolicyId },
        };
        config.EnableEncodePriority = true;

        _events = new EncodePriorityEvents(_sessions.Object, _tasks.Object, Mock.Of<ILogger<EncodePriorityEvents>>(), _time);
        _events.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _events.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _events.Dispose();
        EncodePriorityRuntime.Reset();
        Directory.Delete(_dir, true);
    }

    private void Start(User user, Guid? itemId = null, string? mediaSourceId = null)
    {
        _sessions.Raise(s => s.PlaybackStart += null, _sessions.Object, new PlaybackProgressEventArgs
        {
            Users = new List<User> { user },
            Item = new Movie { Id = itemId ?? Guid.NewGuid() },
            MediaSourceId = mediaSourceId!,
        });
    }

    private void VerifyQueued(int times) => _tasks.Verify(t => t.QueueScheduledTask<EncodePriorityTask>(), Times.Exactly(times));

    [Fact]
    public void TwoStartsInsideTheWindow_QueueOneRun_WhenTheTimerFires()
    {
        Start(_capped);
        Start(_capped);

        Assert.Equal(1, _time.Created);
        Assert.Equal(TimeSpan.FromMinutes(10), _time.LastDue);
        VerifyQueued(0);

        _time.FireAll();
        VerifyQueued(1);
        Assert.Equal("playback", EncodePriorityRuntime.TakeTrigger());

        Start(_capped);
        Assert.Equal(2, _time.Created);
    }

    [Fact]
    public void TheWindowFollowsTheDebounceSetting()
    {
        _plugin.Configuration.PriorityDebounceMinutes = 3;

        Start(_capped);

        Assert.Equal(TimeSpan.FromMinutes(3), _time.LastDue);
    }

    [Fact]
    public void AnUncappedViewer_QueuesNothing()
    {
        Start(_uncapped);

        Assert.Equal(0, _time.Created);
    }

    [Fact]
    public void FeatureOff_QueuesNothing()
    {
        _plugin.Configuration.EnableEncodePriority = false;

        Start(_capped);

        Assert.Equal(0, _time.Created);
    }

    [Fact]
    public void RefreshOnPlaybackOff_QueuesNothing()
    {
        _plugin.Configuration.PriorityRefreshOnPlayback = false;

        Start(_capped);

        Assert.Equal(0, _time.Created);
    }

    [Fact]
    public void SavingTheSettings_QueuesARun_EvenWhenTheFeatureIsSwitchedOff()
    {
        var config = _plugin.Configuration;
        config.EnableEncodePriority = false;

        _plugin.UpdateConfiguration(config);

        VerifyQueued(1);
        Assert.Equal("settings saved", EncodePriorityRuntime.TakeTrigger());
    }

    [Fact]
    public async Task Stopped_NoLongerListens()
    {
        await _events.StopAsync(CancellationToken.None);

        Start(_capped);
        _plugin.UpdateConfiguration(_plugin.Configuration);

        Assert.Equal(0, _time.Created);
        VerifyQueued(0);
    }

    [Fact]
    public void ACappedPlayOfACoveredItem_IsQueuedForTheServedTrail()
    {
        var item = Guid.NewGuid();
        var copy = Guid.NewGuid();
        EncodePriorityRuntime.SetCovered(new[] { new CoveredRecord { ItemId = item, CoveredVersionId = copy } });

        Start(_capped, item, copy.ToString("N"));
        Start(_capped, Guid.NewGuid());
        Start(_uncapped, item, copy.ToString("N"));

        var play = Assert.Single(EncodePriorityRuntime.DrainServed());
        Assert.Equal((item, (Guid?)copy, 720), (play.ItemId, play.MediaSourceId, play.Cap));
        Assert.Empty(EncodePriorityRuntime.DrainServed());
    }

    [Fact]
    public async Task ALibraryScan_QueuesARunOnlyWhenTheFeatureIsOn()
    {
        var postScan = new EncodePriorityPostScanTask(_tasks.Object);

        await postScan.Run(new Progress<double>(), CancellationToken.None);
        VerifyQueued(1);
        Assert.Equal("library scan", EncodePriorityRuntime.TakeTrigger());

        _plugin.Configuration.EnableEncodePriority = false;
        await postScan.Run(new Progress<double>(), CancellationToken.None);
        VerifyQueued(1);
    }

    [Fact]
    public void Uninstalling_RemovesEveryListThePluginWrote()
    {
        var data = Path.Combine(_dir, "data");
        var media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(media);
        var ours = Path.Combine(media, ".encoder-priority.json");
        var byHand = Path.Combine(media, ".by-hand.json");
        PriorityFileWriter.Write(ours, "Shows", new[] { "Show A/x.mkv" }, false, DateTime.UtcNow);
        File.WriteAllText(byHand, "{\"paths\":[]}");
        var state = new EncodePriorityState();
        state.RecordWritten(ours);
        state.RecordWritten(byHand);
        state.Save(EncodePriorityPaths.StateFile(data));

        _plugin.OnUninstalling();

        Assert.False(File.Exists(ours));
        Assert.True(File.Exists(byHand));
        Assert.Empty(EncodePriorityState.Load(EncodePriorityPaths.StateFile(data), out _).WrittenFiles);
    }

    [Fact]
    public void Uninstalling_WithNoStateFile_DoesNotThrowOrCreateOne()
    {
        _plugin.OnUninstalling();

        Assert.False(Directory.Exists(EncodePriorityPaths.Root(Path.Combine(_dir, "data"))));
    }

    /// <summary>A clock whose timers fire only when the test says so.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State)> _timers = new();

        public int Created { get; private set; }

        public TimeSpan LastDue { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Created++;
            LastDue = dueTime;
            _timers.Add((callback, state));
            return new NoopTimer();
        }

        public void FireAll()
        {
            var timers = _timers.ToList();
            _timers.Clear();
            foreach (var (callback, state) in timers)
            {
                callback(state);
            }
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
