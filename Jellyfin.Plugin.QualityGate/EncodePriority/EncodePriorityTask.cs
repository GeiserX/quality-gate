using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QualityGate.EncodePriority;

/// <summary>
/// Builds and writes every encoder's priority list. The only writer of the files and the state.
/// </summary>
/// <remarks>
/// One run: clamp the settings, remove files no target claims any more (always, even with the
/// feature off), then, when the feature is on, collect demand once for every viewer any encoder
/// counts, build each target's list, and only then write them all. A run that goes over its
/// budget writes nothing and keeps every previous file: a partial list would silently reorder the
/// encoder. Jellyfin never runs one task twice at once, so there is no concurrent writer.
/// Found by Jellyfin's export scan, so it needs no registration.
/// </remarks>
public sealed class EncodePriorityTask : IScheduledTask
{
    /// <summary>The key the settings page looks the task up by.</summary>
    public const string TaskKey = "QualityGateEncodePriority";

    /// <summary>The activity log entry type the status panel reads.</summary>
    public const string ActivityType = "QualityGate.EncodePriority";

    /// <summary>How long an unchanged list may sit with no cover before the encoder is suspected.</summary>
    internal static readonly TimeSpan StuckAfter = TimeSpan.FromHours(48);

    /// <summary>How long a covered item is remembered.</summary>
    internal static readonly TimeSpan CoveredKeptFor = TimeSpan.FromDays(7);

    /// <summary>The most covered items remembered.</summary>
    internal const int CoveredKeptMax = 500;

    private static readonly ConcurrentDictionary<string, byte> LoggedWarnings = new();

    private readonly ICandidateCollector _collector;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IActivityManager _activityManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<EncodePriorityTask> _logger;
    private readonly Func<DateTime> _clock;

    /// <summary>Initializes a new instance of the <see cref="EncodePriorityTask"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="tvSeriesManager">The TV series manager.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="activityManager">The activity manager.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public EncodePriorityTask(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ITVSeriesManager tvSeriesManager,
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        IMediaSourceManager mediaSourceManager,
        IActivityManager activityManager,
        IApplicationPaths applicationPaths,
        ILogger<EncodePriorityTask> logger)
        : this(
            new CandidateCollector(libraryManager, tvSeriesManager, sessionManager, userDataManager, mediaSourceManager),
            userManager,
            libraryManager,
            activityManager,
            applicationPaths,
            logger,
            () => DateTime.UtcNow)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncodePriorityTask"/> class with its parts supplied.</summary>
    internal EncodePriorityTask(
        ICandidateCollector collector,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IActivityManager activityManager,
        IApplicationPaths applicationPaths,
        ILogger<EncodePriorityTask> logger,
        Func<DateTime> clock)
    {
        _collector = collector;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _activityManager = activityManager;
        _applicationPaths = applicationPaths;
        _logger = logger;
        _clock = clock;
    }

    /// <inheritdoc />
    public string Name => "Quality Gate: build encode priority lists";

    /// <inheritdoc />
    public string Key => TaskKey;

    /// <inheritdoc />
    public string Description => "Writes a list of what capped viewers are watching so an encoder makes those copies first. Does nothing while encode priority is off, apart from removing lists it wrote earlier.";

    /// <inheritdoc />
    public string Category => "Quality Gate";

    /// <summary>Gets or sets a budget that replaces the configured one, for tests.</summary>
    internal TimeSpan? BudgetOverride { get; set; }

    /// <summary>Gets or sets the folder check, replaceable in tests.</summary>
    internal Func<string, bool> DirectoryExists { get; set; } = Directory.Exists;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks },
            new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var trigger = EncodePriorityRuntime.TakeTrigger();
        if (EncodePriorityRuntime.TakePreview())
        {
            await PreviewAsync(progress, cancellationToken).ConfigureAwait(false);

            // A trigger that arrived together with the preview still gets its real run.
            var rest = string.Join(", ", trigger.Split(", ").Where(t => t != EncodePriorityRuntime.PreviewTrigger));
            if (rest.Length == 0)
            {
                return;
            }

            trigger = rest;
        }

        await RunAsync(trigger, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One run. See the class remarks.</summary>
    /// <param name="trigger">What started it.</param>
    /// <param name="progress">Progress, 0 to 100.</param>
    /// <param name="cancellationToken">The task's own cancellation.</param>
    /// <returns>A task that completes when the run is over.</returns>
    internal async Task RunAsync(string trigger, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return;
        }

        var started = Stopwatch.StartNew();
        var now = _clock();
        var options = ReadOptions(config);
        var dataPath = _applicationPaths.DataPath;
        var statePath = EncodePriorityPaths.StateFile(dataPath);
        var state = LoadState(statePath);
        var libraries = ReadLibraries();
        var locations = libraries.SelectMany(l => l.Locations).ToList();

        // Which file each writing target claims. Two targets may not write one file.
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var outputProblems = new Dictionary<string, Finding>(StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        if (options.Enabled)
        {
            foreach (var target in options.RunnableTargets)
            {
                if (!EncodePriorityPaths.TryResolveOutput(target, dataPath, locations, out var output, out var error))
                {
                    outputProblems[target.Id] = new Finding("OutputInvalid", error);
                    continue;
                }

                if (target.DryRun)
                {
                    outputs[target.Id] = output;
                    continue;
                }

                if (!claimed.Add(output))
                {
                    outputProblems[target.Id] = new Finding("OutputConflict", $"another encoder already writes {output}; give each encoder its own file");
                    continue;
                }

                outputs[target.Id] = output;
            }
        }

        foreach (var (path, outcome) in CleanupPass.Run(state, claimed))
        {
            if (outcome == RemoveOutcome.Failed)
            {
                _logger.LogWarning("QualityGate: could not remove or empty the old priority list {Path}; will retry on the next run", path);
            }
            else
            {
                _logger.LogInformation("QualityGate: old priority list {Path}: {Outcome}", path, outcome);
            }
        }

        var runnable = options.Enabled ? options.RunnableTargets.ToList() : new List<EncodeTargetOptions>();
        foreach (var gone in state.Targets.Keys.Where(id => !runnable.Any(t => t.Id == id)).ToList())
        {
            state.Targets.Remove(gone);
        }

        if (runnable.Count == 0)
        {
            SaveState(state, statePath);
            progress?.Report(100);
            return;
        }

        progress?.Report(5);

        var coveredByTarget = new Dictionary<string, List<CoveredRecord>>(StringComparer.Ordinal);
        var built = CollectAndBuild(options, runnable, config, libraries, now, progress, cancellationToken, (target, build, token) =>
            coveredByTarget[target.Id] = FindCovered(target, build, state, now, token));

        if (built.TimedOut is { } timedOut)
        {
            foreach (var target in runnable)
            {
                var previous = state.Targets.GetValueOrDefault(target.Id) ?? new TargetState();
                var findings = new List<Finding> { timedOut };
                await RecordActivityAsync(target.Name, previous, previous.Entries.Select(e => e.Path).ToList(), previous.Counts, findings).ConfigureAwait(false);
                previous.Name = target.Name;
                previous.LastRunUtc = now;
                previous.Trigger = trigger;
                previous.DurationMs = started.ElapsedMilliseconds;
                previous.Result = "TimedOut";
                previous.Error = null;
                previous.Findings = findings;
                state.Targets[target.Id] = previous;
            }

            SaveState(state, statePath);
            return;
        }

        progress?.Report(80);

        // Every list is built; only now is anything written.
        foreach (var target in runnable)
        {
            var previous = state.Targets.GetValueOrDefault(target.Id);
            var next = new TargetState
            {
                Name = target.Name,
                LastRunUtc = now,
                Trigger = trigger,
                LastWriteUtc = previous?.LastWriteUtc,
                LastChangeUtc = previous?.LastChangeUtc,
                OutputPath = outputs.GetValueOrDefault(target.Id),
            };

            if (built.Failed.TryGetValue(target.Id, out var error))
            {
                next.Result = "Error";
                next.Error = error;
                next.Entries = previous?.Entries ?? new List<StateEntry>();
                next.Counts = previous?.Counts ?? new TargetCounts();
                next.DurationMs = started.ElapsedMilliseconds;
                state.Targets[target.Id] = next;
                continue;
            }

            var build = built.Builds[target.Id];
            var findings = new List<Finding>(build.Findings);
            if (outputProblems.TryGetValue(target.Id, out var problem))
            {
                findings.Add(problem);
            }

            var covered = coveredByTarget.GetValueOrDefault(target.Id) ?? new List<CoveredRecord>();
            foreach (var record in covered)
            {
                state.Covered.Add(record);
            }

            build.Counts.Covered = covered.Count;
            var paths = build.Paths;
            var previousPaths = previous?.Entries.Select(e => e.Path).ToList() ?? new List<string>();
            var listChanged = !paths.SequenceEqual(previousPaths, StringComparer.Ordinal);
            if (listChanged)
            {
                next.LastChangeUtc = now;
            }

            next.Entries = ToStateEntries(build, previous, now);
            next.Counts = build.Counts;

            if (IsStuck(target, next, state, listChanged, now))
            {
                findings.Add(new Finding(
                    "Stuck",
                    "The encoder does not seem to be working on this list. Check that PRIORITY_FILE points at this file as the encoder sees it, that SOURCE_FOLDER is the folder mapped here, and the INFO line the encoder logs when it reloads the list: \"Priority list <file>: N entries, M of P pending files match\". Zero matching files means the folder mapping is wrong."));
            }

            if (target.DryRun)
            {
                next.Result = "DryRun";
            }
            else if (next.OutputPath is { } output)
            {
                var write = PriorityFileWriter.Write(output, target.Name, paths, createDirectory: target.OutputMode == OutputMode.DataFolder, now);
                switch (write.Outcome)
                {
                    case WriteOutcome.Foreign:
                        findings.Add(new Finding("ForeignFile", write.Error ?? output));
                        break;
                    case WriteOutcome.Failed:
                        findings.Add(new Finding(
                            "WriteFailed",
                            $"Could not write {output}: {write.Error}. If the media folder is mounted read-only in Jellyfin, switch this encoder to Data folder and mount that file into the encoder."));
                        break;
                    default:
                        state.RecordWritten(output);
                        if (write.Outcome != WriteOutcome.Unchanged)
                        {
                            next.LastWriteUtc = now;
                        }

                        break;
                }

                next.Result = findings.Count > 0 ? "Warning" : write.Outcome == WriteOutcome.Unchanged ? "Unchanged" : "OK";
                if (write.Outcome is WriteOutcome.Written or WriteOutcome.Heartbeat)
                {
                    _logger.LogInformation(
                        "QualityGate: priority list for {Target} written to {Path}: {Listed} entries, {Viewers} viewers, {Unmapped} unmapped",
                        target.Name,
                        output,
                        build.Counts.Listed,
                        build.Counts.Viewers,
                        build.Counts.Unmapped);
                }
            }
            else
            {
                next.Result = "Warning";
            }

            next.Findings = findings;
            next.DurationMs = started.ElapsedMilliseconds;
            await RecordActivityAsync(target.Name, previous, paths, build.Counts, findings).ConfigureAwait(false);
            state.Targets[target.Id] = next;
        }

        TrimCovered(state, now);
        EncodePriorityRuntime.SetCovered(state.Covered);
        SaveState(state, statePath);
        progress?.Report(100);
    }

    /// <summary>
    /// Builds every enabled encoder's list as a dry run, whether or not the feature is switched on,
    /// and keeps the result in the state's preview slot.
    /// </summary>
    /// <remarks>
    /// It writes no list, removes no list, writes no activity entry and leaves every target's real
    /// last run alone, so the next real run compares against what the encoder actually has.
    /// </remarks>
    /// <param name="progress">Progress, 0 to 100.</param>
    /// <param name="cancellationToken">The task's own cancellation.</param>
    /// <returns>A task that completes when the preview is saved.</returns>
    internal Task PreviewAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Task.CompletedTask;
        }

        var started = Stopwatch.StartNew();
        var now = _clock();
        var options = ReadOptions(config);
        var dataPath = _applicationPaths.DataPath;
        var statePath = EncodePriorityPaths.StateFile(dataPath);
        var state = LoadState(statePath);
        var libraries = ReadLibraries();
        var locations = libraries.SelectMany(l => l.Locations).ToList();
        var targets = options.RunnableTargets.ToList();

        var preview = new PreviewState { RanAtUtc = now, Result = "OK" };
        var built = targets.Count == 0
            ? new BuiltLists()
            : CollectAndBuild(options, targets, config, libraries, now, progress, cancellationToken, null);
        if (built.TimedOut is not null)
        {
            preview.Result = "TimedOut";
        }

        foreach (var target in targets)
        {
            var real = state.Targets.GetValueOrDefault(target.Id);
            var next = new TargetState
            {
                Name = target.Name,
                LastRunUtc = now,
                Trigger = EncodePriorityRuntime.PreviewTrigger,
                OutputPath = EncodePriorityPaths.TryResolveOutput(target, dataPath, locations, out var output, out _) ? output : null,
            };

            if (built.TimedOut is { } timedOut)
            {
                next.Result = "TimedOut";
                next.Findings = new List<Finding> { timedOut };
            }
            else if (built.Failed.TryGetValue(target.Id, out var error))
            {
                next.Result = "Error";
                next.Error = error;
            }
            else
            {
                var build = built.Builds[target.Id];
                next.Result = "DryRun";
                next.Entries = ToStateEntries(build, real, now);
                next.Counts = build.Counts;
                next.Findings = new List<Finding>(build.Findings);
            }

            next.DurationMs = started.ElapsedMilliseconds;
            preview.Targets[target.Id] = next;
        }

        preview.DurationMs = started.ElapsedMilliseconds;
        state.Preview = preview;
        SaveState(state, statePath);
        _logger.LogInformation(
            "QualityGate: encode priority preview built {Targets} lists in {Ms} ms ({Result}); nothing was written",
            targets.Count,
            preview.DurationMs,
            preview.Result);
        progress?.Report(100);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Collects demand once for every viewer the given targets count, then builds each target's
    /// list. A target whose build throws is reported in <see cref="BuiltLists.Failed"/> and the
    /// others still build. Going over the run budget stops everything and is reported in
    /// <see cref="BuiltLists.TimedOut"/>; the task's own cancellation is not swallowed.
    /// </summary>
    private BuiltLists CollectAndBuild(
        EncodePriorityOptions options,
        IReadOnlyList<EncodeTargetOptions> targets,
        PluginConfiguration config,
        IReadOnlyList<LibraryFolder> libraries,
        DateTime now,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        Action<EncodeTargetOptions, TargetBuild, CancellationToken>? afterBuild)
    {
        var allUsers = _userManager.GetUsers().ToList();
        var viewers = allUsers.Select(ToViewer).ToList();
        var union = PriorityListBuilder.AudienceUnion(options, viewers, now);
        var users = allUsers.Where(u => union.Contains(u.Id)).ToList();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(BudgetOverride ?? options.RunBudget);

        var demand = new CollectedDemand();
        var result = new BuiltLists();
        try
        {
            _collector.Collect(options, users, now, budget.Token, demand);
            progress?.Report(60);

            var input = new BuildInput
            {
                Options = options,
                Viewers = viewers,
                Demand = demand,
                Libraries = libraries,
                Policies = config.Policies.Where(p => p.Enabled).Select(p => new PolicySummary(p.Name, p.MaxHeight)).ToList(),
                NowUtc = now,
                DirectoryExists = DirectoryExists,
                ResolveLink = ResolveLink,
            };

            foreach (var target in targets)
            {
                budget.Token.ThrowIfCancellationRequested();
                try
                {
                    var build = PriorityListBuilder.Build(target, input);
                    afterBuild?.Invoke(target, build, budget.Token);
                    result.Builds[target.Id] = build;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "QualityGate: building the priority list for {Target} failed; its previous list is kept", target.Name);
                    result.Failed[target.Id] = ex.Message;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "QualityGate: encode priority run went over its {Budget}s budget after {Processed} of {Total} viewers; every previous list is kept",
                (BudgetOverride ?? options.RunBudget).TotalSeconds,
                demand.UsersProcessed,
                users.Count);
            result.TimedOut = new Finding(
                "TimedOut",
                $"Previous list kept. {demand.UsersProcessed} of {users.Count} viewers processed. Lower Next up shows per viewer or depth.");
        }

        return result;
    }

    private EncodePriorityOptions ReadOptions(PluginConfiguration config)
    {
        var options = EncodePriorityOptions.From(config);
        foreach (var warning in options.Warnings)
        {
            if (LoggedWarnings.TryAdd(warning, 0))
            {
                _logger.LogWarning("QualityGate: encode priority setting corrected: {Warning}", warning);
            }
        }

        return options;
    }

    private EncodePriorityState LoadState(string statePath)
    {
        var state = EncodePriorityState.Load(statePath, out var stateError);
        if (stateError is not null)
        {
            _logger.LogWarning("QualityGate: encode priority state could not be read and starts empty: {Error}", stateError);
        }

        return state;
    }

    private List<LibraryFolder> ReadLibraries()
        => _libraryManager.GetVirtualFolders()
            .Select(f => new LibraryFolder(f.Name ?? string.Empty, (IReadOnlyList<string>?)f.Locations ?? Array.Empty<string>()))
            .ToList();

    /// <summary>Turns a Jellyfin user into what the builder needs, resolving their policy the way playback does.</summary>
    /// <param name="user">The user.</param>
    /// <returns>The viewer.</returns>
    internal static Viewer ToViewer(User user)
    {
        var policy = QualityGateService.GetUserPolicy(user.Id);
        return new Viewer(
            user.Id,
            user.HasPermission(PermissionKind.IsDisabled),
            user.LastActivityDate,
            policy?.MaxHeight ?? 0,
            policy?.Id,
            ReferenceEquals(policy, QualityGateService.DenyAllPolicy));
    }

    private static string? ResolveLink(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<StateEntry> ToStateEntries(TargetBuild build, TargetState? previous, DateTime now)
    {
        var firstListed = previous?.Entries
            .GroupBy(e => e.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().FirstListedAt, StringComparer.Ordinal)
            ?? new Dictionary<string, DateTime>(StringComparer.Ordinal);

        return build.Entries.Select(e => new StateEntry
        {
            Path = e.Path,
            ItemId = e.ItemId,
            VersionId = e.VersionId,
            Height = e.Height,
            GapCap = e.GapCap,
            Tier = e.Tier,
            Depth = e.Depth,
            Users = e.Users,
            FirstListedAt = firstListed.TryGetValue(e.Path, out var first) ? first : now,
            Reasons = e.Reasons,
        }).ToList();
    }

    /// <summary>
    /// The encoder is suspected only when the same list has sat unchanged for two days, its top ten
    /// entries have all been listed that long, and none of the target's gaps was covered meanwhile.
    /// </summary>
    private static bool IsStuck(EncodeTargetOptions target, TargetState next, EncodePriorityState state, bool listChanged, DateTime now)
    {
        if (target.DryRun || listChanged || next.Entries.Count == 0 || next.LastChangeUtc is not { } changed || now - changed < StuckAfter)
        {
            return false;
        }

        if (next.Entries.Take(10).Any(e => now - e.FirstListedAt < StuckAfter))
        {
            return false;
        }

        return !state.Covered.Any(c => c.TargetId == target.Id && now - c.CoveredAt < StuckAfter);
    }

    private static void TrimCovered(EncodePriorityState state, DateTime now)
    {
        state.Covered = state.Covered
            .Where(c => now - c.CoveredAt < CoveredKeptFor)
            .OrderBy(c => c.CoveredAt)
            .TakeLast(CoveredKeptMax)
            .ToList();
    }

    /// <summary>
    /// Finds the items on the previous list that dropped off it because a within-cap version now
    /// exists, as opposed to dropping off because nobody asks for them any more.
    /// </summary>
    private List<CoveredRecord> FindCovered(EncodeTargetOptions target, TargetBuild build, EncodePriorityState state, DateTime now, CancellationToken cancellationToken)
    {
        var covered = new List<CoveredRecord>();
        if (state.Targets.GetValueOrDefault(target.Id) is not { } previous)
        {
            return covered;
        }

        var stillListed = build.Entries.Select(e => e.ItemId).ToHashSet();
        var alreadyCovered = state.Covered.Where(c => c.TargetId == target.Id).Select(c => c.ItemId).ToHashSet();
        foreach (var dropped in previous.Entries.Where(e => !stillListed.Contains(e.ItemId)).GroupBy(e => e.ItemId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (alreadyCovered.Contains(dropped.Key))
            {
                continue;
            }

            var gapCap = dropped.Max(e => e.GapCap);
            var within = _collector.GetVersions(dropped.Key)
                .Where(v => v.Height is int h && h <= gapCap)
                .OrderByDescending(v => v.Height)
                .FirstOrDefault();
            if (within is null)
            {
                continue;
            }

            covered.Add(new CoveredRecord
            {
                ItemId = dropped.Key,
                TargetId = target.Id,
                ListedAt = dropped.Min(e => e.FirstListedAt),
                CoveredAt = now,
                CoveredVersionId = within.Id,
                GapCap = gapCap,
            });
        }

        return covered;
    }

    /// <summary>
    /// Writes at most one activity entry per target per run, and only when the list changed or a
    /// finding appeared or cleared, so the activity log does not fill with identical lines.
    /// </summary>
    private async Task RecordActivityAsync(string name, TargetState? previous, IReadOnlyList<string> paths, TargetCounts counts, List<Finding> findings)
    {
        var previousPaths = previous?.Entries.Select(e => e.Path).ToList() ?? new List<string>();
        var previousCodes = (previous?.Findings ?? new List<Finding>()).Select(f => f.Code).OrderBy(c => c, StringComparer.Ordinal);
        var codes = findings.Select(f => f.Code).OrderBy(c => c, StringComparer.Ordinal);
        if (previous is not null
            && paths.SequenceEqual(previousPaths, StringComparer.Ordinal)
            && codes.SequenceEqual(previousCodes, StringComparer.Ordinal))
        {
            return;
        }

        var added = paths.Except(previousPaths, StringComparer.Ordinal).Count();
        var removed = previousPaths.Except(paths, StringComparer.Ordinal).Count();
        var entry = new ActivityLog("Quality Gate encode priority: " + Truncate(name, 400), ActivityType, Guid.Empty)
        {
            ShortOverview = Truncate($"{name}: {paths.Count} listed (+{added}, -{removed}) · {counts.Viewers} viewers · {counts.Unmapped} unmapped", 512),
            Overview = findings.Count == 0 ? null : Truncate(string.Join("\n", findings.Select(f => f.Code + ": " + f.Message)), 512),
            LogSeverity = findings.Count == 0 ? LogLevel.Information : LogLevel.Warning,
        };

        try
        {
            await _activityManager.CreateAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The status panel loses one line; the list itself is unaffected.
            _logger.LogWarning(ex, "QualityGate: could not write the encode priority activity entry for {Target}", name);
        }
    }

    private void SaveState(EncodePriorityState state, string statePath)
    {
        try
        {
            state.Save(statePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "QualityGate: could not save encode priority state to {Path}", statePath);
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    /// <summary>What one collect-and-build pass produced.</summary>
    private sealed class BuiltLists
    {
        /// <summary>Gets each target's list, keyed by target id.</summary>
        public Dictionary<string, TargetBuild> Builds { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the error of each target whose build threw.</summary>
        public Dictionary<string, string> Failed { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets or sets the finding when the pass went over its budget; null when it did not.</summary>
        public Finding? TimedOut { get; set; }
    }
}
