using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.QualityGate.EncodePriority;

/// <summary>A capped viewer starting a covered item, recorded in memory until the next run folds it in.</summary>
/// <param name="ItemId">The item the client opened.</param>
/// <param name="MediaSourceId">The version actually played, when the client said.</param>
/// <param name="Cap">The viewer's cap.</param>
/// <param name="PlayedAt">When playback started.</param>
internal sealed record ServedPlay(Guid ItemId, Guid? MediaSourceId, int Cap, DateTime PlayedAt);

/// <summary>
/// The little in-memory state shared between the triggers and the scheduled task.
/// </summary>
/// <remarks>
/// Jellyfin's task manager runs a task with no arguments, so whoever queues a run records why
/// here and the run takes it. Nothing here is persisted; a restart loses at most the label of a
/// run that was about to happen anyway.
/// </remarks>
internal static class EncodePriorityRuntime
{
    /// <summary>The label for a run nobody asked for explicitly: the interval or startup trigger.</summary>
    public const string ScheduledTrigger = "scheduled";

    /// <summary>The label for a run the admin asked for as a preview.</summary>
    public const string PreviewTrigger = "preview";

    /// <summary>The most plays held between two runs; older ones are dropped first.</summary>
    public const int ServedQueueMax = 1000;

    private static readonly object Gate = new();
    private static readonly Queue<ServedPlay> Served = new();
    private static string? _pendingTrigger;
    private static bool _previewPending;
    private static HashSet<Guid> _coveredIds = new();

    /// <summary>Records why the next run is happening. Each label appears once, in arrival order.</summary>
    /// <param name="trigger">A short label.</param>
    public static void RequestRun(string trigger)
    {
        lock (Gate)
        {
            // Compared per label, not against the whole string: while a long run is going,
            // alternating triggers would otherwise append again on every request.
            if (_pendingTrigger is null)
            {
                _pendingTrigger = trigger;
            }
            else if (!_pendingTrigger.Split(", ").Contains(trigger, StringComparer.Ordinal))
            {
                _pendingTrigger += ", " + trigger;
            }
        }
    }

    /// <summary>Asks the next run to build every enabled encoder's list as a dry run first.</summary>
    public static void RequestPreview()
    {
        lock (Gate)
        {
            _previewPending = true;
        }

        RequestRun(PreviewTrigger);
    }

    /// <summary>Takes and clears the preview request.</summary>
    /// <returns>True when a preview was asked for since the last run took it.</returns>
    public static bool TakePreview()
    {
        lock (Gate)
        {
            var pending = _previewPending;
            _previewPending = false;
            return pending;
        }
    }

    /// <summary>
    /// Publishes the covered items the playback handler watches for. Items and every version id,
    /// so a play of the primary or of the new copy both match.
    /// </summary>
    /// <param name="covered">The covered records.</param>
    public static void SetCovered(IEnumerable<CoveredRecord> covered)
    {
        var ids = new HashSet<Guid>();
        foreach (var record in covered)
        {
            ids.Add(record.ItemId);
            ids.Add(record.CoveredVersionId);
        }

        lock (Gate)
        {
            _coveredIds = ids;
        }
    }

    /// <summary>Checks, in memory, whether an item or version is one a run recorded as covered.</summary>
    /// <param name="id">The item or version id.</param>
    /// <returns>True when it is covered.</returns>
    public static bool IsCovered(Guid id)
    {
        lock (Gate)
        {
            return _coveredIds.Contains(id);
        }
    }

    /// <summary>Records a play of a covered item, dropping the oldest when the queue is full.</summary>
    /// <param name="play">The play.</param>
    public static void RecordServed(ServedPlay play)
    {
        lock (Gate)
        {
            if (Served.Count >= ServedQueueMax)
            {
                Served.Dequeue();
            }

            Served.Enqueue(play);
        }
    }

    /// <summary>Takes every recorded play, leaving the queue empty.</summary>
    /// <returns>The plays, oldest first.</returns>
    public static IReadOnlyList<ServedPlay> DrainServed()
    {
        lock (Gate)
        {
            var plays = Served.ToList();
            Served.Clear();
            return plays;
        }
    }

    /// <summary>Puts plays back at the front of the queue, for a run that could not fold them in.</summary>
    /// <param name="plays">The plays, oldest first.</param>
    public static void Requeue(IReadOnlyList<ServedPlay> plays)
    {
        lock (Gate)
        {
            var merged = plays.Concat(Served).TakeLast(ServedQueueMax).ToList();
            Served.Clear();
            foreach (var play in merged)
            {
                Served.Enqueue(play);
            }
        }
    }

    /// <summary>Clears everything, for tests.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _pendingTrigger = null;
            _previewPending = false;
            _coveredIds = new HashSet<Guid>();
            Served.Clear();
        }
    }

    /// <summary>Takes and clears the recorded reason.</summary>
    /// <returns>The reason, or <see cref="ScheduledTrigger"/> when none was recorded.</returns>
    public static string TakeTrigger()
    {
        lock (Gate)
        {
            var trigger = _pendingTrigger ?? ScheduledTrigger;
            _pendingTrigger = null;
            return trigger;
        }
    }
}
