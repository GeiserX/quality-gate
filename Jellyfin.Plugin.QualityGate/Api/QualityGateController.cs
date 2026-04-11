using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QualityGate.Api;

/// <summary>
/// API controller for Quality Gate filtered playback info.
/// Uses the authenticated caller's identity — no caller-supplied userId.
/// </summary>
[ApiController]
[Route("QualityGate")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class QualityGateController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILogger<QualityGateController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="QualityGateController"/> class.
    /// </summary>
    public QualityGateController(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        ILogger<QualityGateController> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets filtered media sources for an item based on the authenticated caller's quality policy.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <returns>Filtered list of media sources.</returns>
    [HttpGet("MediaSources/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<MediaSourceInfo>>> GetFilteredMediaSources(
        [FromRoute, Required] Guid itemId)
    {
        var userId = GetAuthenticatedUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return NotFound();
        }

        var sources = await _mediaSourceManager.GetPlaybackMediaSources(
            item,
            null,
            true,
            false,
            CancellationToken.None).ConfigureAwait(false);

        var sourceList = sources.ToList();

        var policy = QualityGateService.GetUserPolicy(userId);
        if (policy == null)
        {
            _logger.LogDebug("QualityGate API: No policy for user {UserId}, returning all {Count} sources",
                (object)userId, sourceList.Count);
            return Ok(sourceList);
        }

        var filteredSources = sourceList
            .Where(source => QualityGateService.IsSourcePlayable(policy, source.Path))
            .ToList();

        _logger.LogInformation(
            "QualityGate API: Filtered sources for user {UserId} (policy: {Policy}) - {Original} -> {Filtered}",
            (object)userId, policy.Name, sourceList.Count, filteredSources.Count);

        return Ok(filteredSources);
    }

    /// <summary>
    /// Gets the default (preferred) media source for an item based on the caller's policy.
    /// Returns the first allowed source.
    /// </summary>
    [HttpGet("DefaultSource/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MediaSourceInfo>> GetDefaultSource(
        [FromRoute, Required] Guid itemId)
    {
        var result = await GetFilteredMediaSources(itemId).ConfigureAwait(false);

        if (result.Result is UnauthorizedResult)
        {
            return Unauthorized();
        }

        if (result.Result is NotFoundResult)
        {
            return NotFound();
        }

        var sources = result.Value?.ToList();
        if (sources == null || sources.Count == 0)
        {
            return NotFound("No allowed media sources found");
        }

        return Ok(sources.First());
    }

    private Guid GetAuthenticatedUserId()
    {
        var claim = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)
                 ?? HttpContext.User.FindFirst("Jellyfin-UserId");
        if (claim != null && Guid.TryParse(claim.Value, out var userId) && userId != Guid.Empty)
        {
            return userId;
        }

        return Guid.Empty;
    }
}
