using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QualityGate.EncodePriority;

/// <summary>
/// Queues a priority run when a capped viewer starts something new or the settings are saved,
/// and notes plays of covered items for the served trail.
/// </summary>
/// <remarks>
/// It listens to the legacy <c>ISessionManager.PlaybackStart</c> event, which Jellyfin raises on
/// a background task, rather than <c>IEventConsumer&lt;PlaybackStartEventArgs&gt;</c>, which is
/// awaited inline while the client's playback-start report waits. The handler does in-memory
/// checks only and never touches the database. Playback-triggered runs are debounced with a fixed
/// window: the first start arms a timer, later starts inside the window do nothing, and the run
/// is queued when the timer fires. Progress reports are ignored.
///
/// Settings changes are handled here rather than in the <see cref="Plugin"/> constructor, which
/// would otherwise need an <see cref="ITaskManager"/> that ten test files build without.
/// </remarks>
public sealed class EncodePriorityEvents : IHostedService, IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<EncodePriorityEvents> _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private ITimer? _debounce;
    private Plugin? _plugin;

    /// <summary>Initializes a new instance of the <see cref="EncodePriorityEvents"/> class.</summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="taskManager">The task manager.</param>
    /// <param name="logger">The logger.</param>
    public EncodePriorityEvents(ISessionManager sessionManager, ITaskManager taskManager, ILogger<EncodePriorityEvents> logger)
        : this(sessionManager, taskManager, logger, TimeProvider.System)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncodePriorityEvents"/> class with a clock, for tests.</summary>
    internal EncodePriorityEvents(ISessionManager sessionManager, ITaskManager taskManager, ILogger<EncodePriorityEvents> logger, TimeProvider time)
    {
        _sessionManager = sessionManager;
        _taskManager = taskManager;
        _logger = logger;
        _time = time;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _plugin = Plugin.Instance;
        if (_plugin is not null)
        {
            // A settable delegate property, not an event: += keeps whoever else subscribed.
            _plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        if (_plugin is not null)
        {
            _plugin.ConfigurationChanged -= OnConfigurationChanged;
            _plugin = null;
        }

        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    /// <summary>The playback start handler. In-memory checks only.</summary>
    /// <param name="sender">The session manager.</param>
    /// <param name="e">The playback details.</param>
    internal void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.EnableEncodePriority || e is null)
            {
                return;
            }

            var cap = (e.Users ?? new()).Select(u => CapOf(u.Id)).DefaultIfEmpty(0).Max();
            if (cap <= 0)
            {
                return;
            }

            NoteServed(e, cap);

            if (config.PriorityRefreshOnPlayback)
            {
                Arm(TimeSpan.FromMinutes(Math.Clamp(config.PriorityDebounceMinutes, 1, 240)));
            }
        }
        catch (Exception ex)
        {
            // Raised on a background task: a throw here would only be logged by Jellyfin anyway.
            _logger.LogWarning(ex, "QualityGate: encode priority playback handler failed");
        }
    }

    /// <summary>The settings-saved handler: queue a run so the admin sees the effect straight away.</summary>
    /// <param name="sender">The plugin.</param>
    /// <param name="e">The new configuration.</param>
    internal void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        // Queued even when the feature was just switched off: that run removes the old files.
        Queue("settings saved");
    }

    /// <summary>The cap a user plays under, 0 when uncapped or on a deleted or disabled policy.</summary>
    private static int CapOf(Guid userId)
    {
        var policy = QualityGateService.GetUserPolicy(userId);
        return policy is null || ReferenceEquals(policy, QualityGateService.DenyAllPolicy) ? 0 : Math.Max(0, policy.MaxHeight);
    }

    private static void NoteServed(PlaybackProgressEventArgs e, int cap)
    {
        var itemId = e.Item?.Id ?? Guid.Empty;
        Guid? sourceId = Guid.TryParse(e.MediaSourceId, out var parsed) ? parsed : null;
        if (itemId == Guid.Empty
            || !(EncodePriorityRuntime.IsCovered(itemId) || (sourceId is { } s && EncodePriorityRuntime.IsCovered(s))))
        {
            return;
        }

        EncodePriorityRuntime.RecordServed(new ServedPlay(itemId, sourceId, cap, DateTime.UtcNow));
    }

    private void Arm(TimeSpan window)
    {
        lock (_gate)
        {
            if (_debounce is not null)
            {
                return;
            }

            _debounce = _time.CreateTimer(_ => Fire(), null, window, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
        }

        Queue("playback");
    }

    private void Queue(string trigger)
    {
        EncodePriorityRuntime.RequestRun(trigger);
        _taskManager.QueueScheduledTask<EncodePriorityTask>();
    }
}

/// <summary>Queues a priority run after every library scan, so covered items leave the list soon after their copy appears.</summary>
/// <remarks>Found by Jellyfin's export scan, so it needs no registration.</remarks>
public sealed class EncodePriorityPostScanTask : ILibraryPostScanTask
{
    private readonly ITaskManager _taskManager;

    /// <summary>Initializes a new instance of the <see cref="EncodePriorityPostScanTask"/> class.</summary>
    /// <param name="taskManager">The task manager.</param>
    public EncodePriorityPostScanTask(ITaskManager taskManager)
    {
        _taskManager = taskManager;
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration is PluginConfiguration { EnableEncodePriority: true })
        {
            EncodePriorityRuntime.RequestRun("library scan");
            _taskManager.QueueScheduledTask<EncodePriorityTask>();
        }

        progress?.Report(100);
        return Task.CompletedTask;
    }
}
