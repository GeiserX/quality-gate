using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.QualityGate.EncodePriority;

/// <summary>
/// What the plugin remembers between runs, in <c>&lt;data&gt;/quality-gate/encode-priority/state.json</c>.
/// </summary>
/// <remarks>
/// It is not configuration, so the settings page's whole-object save can never overwrite it. It
/// holds item and user ids and encoder-relative paths, never user names. Only the scheduled task
/// writes it, so there is no concurrent writer.
/// </remarks>
internal sealed class EncodePriorityState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Gets or sets the format version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets every priority file the plugin has written and not yet removed.</summary>
    public List<string> WrittenFiles { get; set; } = new();

    /// <summary>Gets or sets the last run of each target, keyed by target id.</summary>
    public Dictionary<string, TargetState> Targets { get; set; } = new();

    /// <summary>Gets or sets the listed items that have since gained a within-cap version, newest last.</summary>
    public List<CoveredRecord> Covered { get; set; } = new();

    /// <summary>
    /// Gets or sets the last preview: every enabled encoder's list built as a dry run. Null until
    /// the admin asks for one. It never replaces a target's real last run.
    /// </summary>
    public PreviewState? Preview { get; set; }

    /// <summary>Loads the state, or starts empty when there is none or it cannot be read.</summary>
    /// <param name="path">The state file.</param>
    /// <param name="error">Why an existing file could not be read.</param>
    /// <returns>The state.</returns>
    public static EncodePriorityState Load(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                return new EncodePriorityState();
            }

            var state = JsonSerializer.Deserialize<EncodePriorityState>(File.ReadAllBytes(path), JsonOptions);
            return state?.Normalized() ?? new EncodePriorityState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
            return new EncodePriorityState();
        }
    }

    /// <summary>Saves the state atomically, creating its folder when needed.</summary>
    /// <param name="path">The state file.</param>
    public void Save(string path) => PriorityFileWriter.WriteJsonAtomic(path, this, createDirectory: true);

    /// <summary>Records a file the plugin now owns, once.</summary>
    /// <param name="path">The file.</param>
    public void RecordWritten(string path)
    {
        if (!WrittenFiles.Contains(path, StringComparer.Ordinal))
        {
            WrittenFiles.Add(path);
        }
    }

    /// <summary>Replaces anything a hand edit left null with an empty value.</summary>
    /// <returns>This state.</returns>
    internal EncodePriorityState Normalized()
    {
        WrittenFiles = (WrittenFiles ?? new List<string>()).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).ToList();
        Targets ??= new Dictionary<string, TargetState>();
        Covered = (Covered ?? new List<CoveredRecord>()).Where(c => c is not null).ToList();
        if (Preview is not null)
        {
            Preview.Targets ??= new Dictionary<string, TargetState>();
        }

        foreach (var target in Targets.Values.Concat(Preview?.Targets.Values ?? Enumerable.Empty<TargetState>()).Where(t => t is not null))
        {
            target.Entries ??= new List<StateEntry>();
            target.Findings ??= new List<Finding>();
            target.Counts ??= new TargetCounts();
        }

        return this;
    }
}

/// <summary>The removal of what the plugin wrote and no longer claims.</summary>
internal static class CleanupPass
{
    /// <summary>
    /// Removes every recorded file that no enabled, writing target of an enabled feature claims.
    /// </summary>
    /// <remarks>
    /// The encoder never expires a list it has read, so a file left behind keeps reordering its
    /// queue forever. This covers the feature switched off, a target disabled, removed, moved to
    /// another output or switched to dry run, and uninstall. A file that no longer carries the
    /// plugin's marker is left alone and forgotten. A file that could not be removed or emptied
    /// stays recorded, so the next run tries again.
    /// </remarks>
    /// <param name="state">The state, updated in place.</param>
    /// <param name="claimed">The output files the current configuration writes.</param>
    /// <returns>Each file the pass acted on, and what happened to it.</returns>
    public static IReadOnlyList<(string Path, RemoveOutcome Outcome)> Run(EncodePriorityState state, IReadOnlySet<string> claimed)
    {
        var results = new List<(string, RemoveOutcome)>();
        foreach (var path in state.WrittenFiles.ToList())
        {
            if (claimed.Contains(path))
            {
                continue;
            }

            var outcome = PriorityFileWriter.Remove(path);
            results.Add((path, outcome));
            if (outcome is RemoveOutcome.Deleted or RemoveOutcome.Missing or RemoveOutcome.NotOurs)
            {
                state.WrittenFiles.Remove(path);
            }
        }

        return results;
    }
}

/// <summary>The last run of one target, as the status panel shows it.</summary>
internal sealed class TargetState
{
    /// <summary>Gets or sets the target's name at the time of the run.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the file the target writes, as Jellyfin sees it.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets when the last run started.</summary>
    public DateTime? LastRunUtc { get; set; }

    /// <summary>Gets or sets what started it: scheduled, startup, library scan, playback, settings saved.</summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>Gets or sets how long the run took.</summary>
    public long DurationMs { get; set; }

    /// <summary>Gets or sets the badge: OK, Unchanged, Warning, Error, TimedOut or DryRun.</summary>
    public string Result { get; set; } = string.Empty;

    /// <summary>Gets or sets the error, when the result is Error.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets when the file was last written, heartbeat included.</summary>
    public DateTime? LastWriteUtc { get; set; }

    /// <summary>Gets or sets when the list's paths last changed.</summary>
    public DateTime? LastChangeUtc { get; set; }

    /// <summary>Gets or sets the list as last built, in file order.</summary>
    public List<StateEntry> Entries { get; set; } = new();

    /// <summary>Gets or sets the counts.</summary>
    public TargetCounts Counts { get; set; } = new();

    /// <summary>Gets or sets the findings.</summary>
    public List<Finding> Findings { get; set; } = new();
}

/// <summary>The last preview run, which built every enabled encoder's list and wrote nothing.</summary>
internal sealed class PreviewState
{
    /// <summary>Gets or sets when the preview ran.</summary>
    public DateTime RanAtUtc { get; set; }

    /// <summary>Gets or sets how long it took.</summary>
    public long DurationMs { get; set; }

    /// <summary>Gets or sets the outcome: OK or TimedOut.</summary>
    public string Result { get; set; } = string.Empty;

    /// <summary>Gets or sets each encoder's would-be list, keyed by target id.</summary>
    public Dictionary<string, TargetState> Targets { get; set; } = new();
}

/// <summary>One listed file in the state, with when it first appeared.</summary>
internal sealed class StateEntry
{
    /// <summary>Gets or sets the path as the encoder sees it.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the item a viewer asked for.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the version the file is.</summary>
    public Guid VersionId { get; set; }

    /// <summary>Gets or sets that version's height.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets the tallest cap for which the item was a gap.</summary>
    public int GapCap { get; set; }

    /// <summary>Gets or sets the tier.</summary>
    public int Tier { get; set; }

    /// <summary>Gets or sets the depth.</summary>
    public int Depth { get; set; }

    /// <summary>Gets or sets how many viewers asked for it.</summary>
    public int Users { get; set; }

    /// <summary>Gets or sets when it was first listed.</summary>
    public DateTime FirstListedAt { get; set; }

    /// <summary>Gets or sets why it is listed.</summary>
    public List<string> Reasons { get; set; } = new();
}

/// <summary>A listed item that gained a within-cap version.</summary>
internal sealed class CoveredRecord
{
    /// <summary>Gets or sets the item.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the target whose list it was on.</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>Gets or sets when it was first listed.</summary>
    public DateTime ListedAt { get; set; }

    /// <summary>Gets or sets when a run found the within-cap version.</summary>
    public DateTime CoveredAt { get; set; }

    /// <summary>Gets or sets the within-cap version playback is expected to serve.</summary>
    public Guid CoveredVersionId { get; set; }

    /// <summary>Gets or sets the tallest cap for which the item was a gap.</summary>
    public int GapCap { get; set; }

    /// <summary>Gets or sets when a capped viewer first played it on a version within their cap.</summary>
    public DateTime? ServedAt { get; set; }

    /// <summary>Gets or sets the version that play used.</summary>
    public Guid? ServedVersionId { get; set; }

    /// <summary>Gets or sets when a capped viewer first played it on a version above their cap.</summary>
    public DateTime? ServedOverCapAt { get; set; }

    /// <summary>Gets or sets the version that play used.</summary>
    public Guid? ServedOverCapVersionId { get; set; }
}
