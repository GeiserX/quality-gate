using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.QualityGate.EncodePriority;

/// <summary>
/// The encode priority status and preview, for the settings page. Administrators only.
/// </summary>
/// <remarks>
/// Read-only apart from queueing a preview run, and not on the playback path. Found by Jellyfin's
/// assembly scan of plugin controllers, so it needs no registration. The owner approved these
/// two routes, which the repository lists as an ask-first change.
/// </remarks>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("QualityGate/EncodePriority")]
public sealed class EncodePriorityController : ControllerBase
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;

    /// <summary>Initializes a new instance of the <see cref="EncodePriorityController"/> class.</summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="libraryManager">The library manager, for item titles.</param>
    /// <param name="taskManager">The task manager.</param>
    public EncodePriorityController(IApplicationPaths applicationPaths, ILibraryManager libraryManager, ITaskManager taskManager)
    {
        _applicationPaths = applicationPaths;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
    }

    /// <summary>
    /// Gets the state the scheduled task keeps: each encoder's last run, its list with the reason
    /// for every entry, the last preview, and the covered items. Titles are looked up now; the
    /// state itself holds only ids.
    /// </summary>
    /// <response code="200">The status.</response>
    /// <returns>The status as JSON.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ContentResult GetStatus()
    {
        var dataPath = _applicationPaths.DataPath;
        var state = EncodePriorityState.Load(EncodePriorityPaths.StateFile(dataPath), out _);
        var titles = new Dictionary<Guid, string?>();
        var json = EncodePriorityStatus.ToJson(state, dataPath, id =>
        {
            if (!titles.TryGetValue(id, out var title))
            {
                title = TitleOf(id);
                titles[id] = title;
            }

            return title;
        });

        return Content(json, MediaTypeNames.Application.Json);
    }

    /// <summary>
    /// Queues a preview: every enabled encoder's list is built as a dry run and kept for the page.
    /// No list is written or removed.
    /// </summary>
    /// <response code="202">The preview run is queued.</response>
    /// <returns>An accepted result.</returns>
    [HttpPost("Preview")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public AcceptedResult Preview()
    {
        EncodePriorityRuntime.RequestPreview();
        _taskManager.QueueScheduledTask<EncodePriorityTask>();
        return Accepted();
    }

    private string? TitleOf(Guid id)
    {
        var item = id == Guid.Empty ? null : _libraryManager.GetItemById(id);
        return item switch
        {
            null => null,
            Episode episode => $"{episode.SeriesName} S{episode.ParentIndexNumber ?? 0:00}E{episode.IndexNumber ?? 0:00} {episode.Name}".Trim(),
            _ when item.ProductionYear is int year => $"{item.Name} ({year})",
            _ => item.Name,
        };
    }
}

/// <summary>The shape the status endpoint returns, built from the state file.</summary>
internal static class EncodePriorityStatus
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>Serialises the state for the page, with a title looked up for every item.</summary>
    /// <param name="state">The state.</param>
    /// <param name="dataPath">Jellyfin's data folder, so the page can show Data folder paths.</param>
    /// <param name="title">Looks up an item's title, or null when the item is gone.</param>
    /// <returns>The JSON.</returns>
    public static string ToJson(EncodePriorityState state, string dataPath, Func<Guid, string?> title)
    {
        var response = new StatusResponse(
            dataPath,
            state.Targets.Select(t => ToTarget(t.Key, t.Value, title)).ToList(),
            state.Preview is { } preview
                ? new StatusPreview(preview.RanAtUtc, preview.DurationMs, preview.Result, preview.Targets.Select(t => ToTarget(t.Key, t.Value, title)).ToList())
                : null,
            state.Covered.Select(c => new StatusCovered(
                c.ItemId,
                title(c.ItemId),
                c.TargetId,
                c.ListedAt,
                c.CoveredAt,
                c.CoveredVersionId,
                c.GapCap,
                c.ServedAt,
                c.ServedVersionId,
                c.ServedOverCapAt)).ToList());

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    private static StatusTarget ToTarget(string id, TargetState target, Func<Guid, string?> title) => new(
        id,
        target.Name,
        target.OutputPath,
        target.LastRunUtc,
        target.Trigger,
        target.DurationMs,
        target.Result,
        target.Error,
        target.LastWriteUtc,
        target.LastChangeUtc,
        target.Counts,
        target.Findings,
        target.Entries.Select((e, i) => new StatusEntry(
            i + 1,
            e.Path,
            title(e.ItemId),
            e.ItemId,
            e.VersionId,
            e.Height,
            e.GapCap,
            e.Tier,
            e.Depth,
            e.Users,
            e.FirstListedAt,
            e.Reasons)).ToList());

    private sealed record StatusResponse(string DataPath, IReadOnlyList<StatusTarget> Targets, StatusPreview? Preview, IReadOnlyList<StatusCovered> Covered);

    private sealed record StatusPreview(DateTime RanAtUtc, long DurationMs, string Result, IReadOnlyList<StatusTarget> Targets);

    private sealed record StatusTarget(
        string Id,
        string Name,
        string? OutputPath,
        DateTime? LastRunUtc,
        string Trigger,
        long DurationMs,
        string Result,
        string? Error,
        DateTime? LastWriteUtc,
        DateTime? LastChangeUtc,
        TargetCounts Counts,
        IReadOnlyList<Finding> Findings,
        IReadOnlyList<StatusEntry> Entries);

    private sealed record StatusEntry(
        int Rank,
        string Path,
        string? Title,
        Guid ItemId,
        Guid VersionId,
        int? Height,
        int GapCap,
        int Tier,
        int Depth,
        int Users,
        DateTime FirstListedAt,
        IReadOnlyList<string> Reasons);

    private sealed record StatusCovered(
        Guid ItemId,
        string? Title,
        string TargetId,
        DateTime ListedAt,
        DateTime CoveredAt,
        Guid CoveredVersionId,
        int GapCap,
        DateTime? ServedAt,
        Guid? ServedVersionId,
        DateTime? ServedOverCapAt);
}
