namespace Jellyfin.Plugin.QualityGate.EncodePriority;

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

    private static readonly object Gate = new();
    private static string? _pendingTrigger;

    /// <summary>Records why the next run is happening.</summary>
    /// <param name="trigger">A short label.</param>
    public static void RequestRun(string trigger)
    {
        lock (Gate)
        {
            _pendingTrigger = _pendingTrigger is null || _pendingTrigger == trigger
                ? trigger
                : _pendingTrigger + ", " + trigger;
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
