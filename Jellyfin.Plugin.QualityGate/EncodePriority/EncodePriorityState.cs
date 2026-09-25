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
