using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QualityGate.Api;

/// <summary>
/// API controller for Quality Gate filtered playback info.
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
    /// Gets filtered media sources for an item based on user's quality policy.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <param name="userId">The user ID.</param>
    /// <returns>Filtered list of media sources.</returns>
    [HttpGet("MediaSources/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<MediaSourceInfo>>> GetFilteredMediaSources(
        [FromRoute, Required] Guid itemId,
        [FromQuery, Required] Guid userId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return NotFound();
        }

        // Get all media sources for the item
        var sources = await _mediaSourceManager.GetPlaybackMediaSources(
            item, 
            null, // user
            true, // allowMediaProbe
            false, // enablePathSubstitution  
            CancellationToken.None).ConfigureAwait(false);

        var sourceList = sources.ToList();
        
        // Get user's policy
        var policy = QualityGateService.GetUserPolicy(userId);
        if (policy == null)
        {
            // No restrictions
            _logger.LogDebug("QualityGate API: No policy for user {UserId}, returning all {Count} sources", 
                userId, sourceList.Count);
            return Ok(sourceList);
        }

        // Filter sources based on policy
        var filteredSources = sourceList
            .Where(source => QualityGateService.IsPathAllowed(policy, source.Path))
            .ToList();

        _logger.LogInformation(
            "QualityGate API: Filtered sources for user {UserId} (policy: {Policy}) - {Original} -> {Filtered}",
            userId, policy.Name, sourceList.Count, filteredSources.Count);

        return Ok(filteredSources);
    }

    /// <summary>
    /// Gets the default (preferred) media source for an item based on user's policy.
    /// Returns the first allowed source.
    /// </summary>
    [HttpGet("DefaultSource/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MediaSourceInfo>> GetDefaultSource(
        [FromRoute, Required] Guid itemId,
        [FromQuery, Required] Guid userId)
    {
        var result = await GetFilteredMediaSources(itemId, userId).ConfigureAwait(false);
        
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
}
