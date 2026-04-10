using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Providers;
using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QualityGate.Filters;

/// <summary>
/// MVC result filter that removes blocked MediaSources from API responses
/// before serialization. Operates on C# objects inside the MVC pipeline,
/// completely bypassing the response compression that broke the middleware approach.
/// </summary>
public class MediaSourceResultFilter : IAsyncResultFilter
{
    private readonly ILogger<MediaSourceResultFilter> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSourceResultFilter"/> class.
    /// </summary>
    public MediaSourceResultFilter(
        ILogger<MediaSourceResultFilter> logger,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
    }

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        var hasItems = path.Contains("/Items/", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("/Items", StringComparison.OrdinalIgnoreCase);
        var isRelevant = path.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase)
                      || (hasItems
                          && path.Contains("/Users/", StringComparison.OrdinalIgnoreCase)
                          && !path.EndsWith("/Intros", StringComparison.OrdinalIgnoreCase));

        if (isRelevant)
        {
            _logger.LogInformation(
                "QualityGate: ResultFilter fired for {Path}, Result type: {ResultType}",
                path, context.Result?.GetType().Name ?? "null");
        }

        if (isRelevant && context.Result is ObjectResult { Value: not null } objectResult)
        {
            var userId = GetUserId(context.HttpContext);
            if (userId != Guid.Empty)
            {
                var policy = QualityGateService.GetUserPolicy(userId);
                if (policy != null)
                {
                    _logger.LogDebug(
                        "QualityGate: Applying policy {Policy} for user {User}",
                        policy.Name, (object)userId);
                    FilterResult(objectResult, policy, userId);
                }
            }
        }

        await next().ConfigureAwait(false);
    }

    private void FilterResult(ObjectResult result, QualityPolicy policy, Guid userId)
    {
        switch (result.Value)
        {
            case PlaybackInfoResponse playbackInfo when playbackInfo.MediaSources?.Any() == true:
            {
                // Skip filtering for intro videos — they must always be playable
                if (playbackInfo.MediaSources.Any(s => IsConfiguredIntroPath(s.Path)))
                {
                    _logger.LogDebug("QualityGate: Skipping filter for intro video playback");
                    break;
                }

                var original = playbackInfo.MediaSources.ToList();
                var filtered = original
                    .Where(s => QualityGateService.IsPathAllowed(policy, s.Path))
                    .ToArray();

                _logger.LogInformation(
                    "QualityGate: Filtered PlaybackInfo for user {User} (policy: {Policy}) - {Original} to {Filtered} sources",
                    (object)userId, policy.Name, original.Count, filtered.Length);
                playbackInfo.MediaSources = filtered;

                if (filtered.Length == 0 && original.Count > 0)
                {
                    playbackInfo.ErrorCode = MediaBrowser.Model.Dlna.PlaybackErrorCode.NotAllowed;
                    _logger.LogInformation(
                        "QualityGate: All sources blocked for user {User} (policy: {Policy}) — returning NotAllowed",
                        (object)userId, policy.Name);
                }

                break;
            }

            case BaseItemDto itemDto when itemDto.MediaSources?.Any() == true:
            {
                var original = itemDto.MediaSources.ToList();
                var filtered = original
                    .Where(s => QualityGateService.IsPathAllowed(policy, s.Path))
                    .ToArray();

                _logger.LogInformation(
                    "QualityGate: Filtered item sources for user {User} (policy: {Policy}) - {Original} to {Filtered} sources",
                    (object)userId, policy.Name, original.Count, filtered.Length);
                itemDto.MediaSources = filtered;

                break;
            }

            case QueryResult<BaseItemDto> queryResult when queryResult.Items?.Any() == true:
            {
                var itemsToRemove = queryResult.Items.Where(item => ShouldHideItem(item, policy, userId)).ToList();

                if (itemsToRemove.Count > 0)
                {
                    var filtered = queryResult.Items.Except(itemsToRemove).ToArray();
                    _logger.LogInformation(
                        "QualityGate: Hid {Count} fully-blocked items from list for user {User} (policy: {Policy})",
                        itemsToRemove.Count, (object)userId, policy.Name);
                    queryResult.Items = filtered;
                    queryResult.TotalRecordCount -= itemsToRemove.Count;
                }

                break;
            }
        }

        // Handle IEnumerable<BaseItemDto> — catches lazy enumerables (e.g. ListSelectIterator)
        // from endpoints like /Items/Latest. Must materialize the sequence, check each item
        // against the policy, remove fully-blocked items, and replace the result value.
        if (result.Value is IEnumerable<BaseItemDto> itemEnumerable
            && result.Value is not BaseItemDto
            && result.Value is not PlaybackInfoResponse
            && result.Value is not QueryResult<BaseItemDto>)
        {
            var materialized = itemEnumerable.ToList();
            var enumItemsToRemove = materialized.Where(item => ShouldHideItem(item, policy, userId)).ToList();

            if (enumItemsToRemove.Count > 0)
            {
                foreach (var toRemove in enumItemsToRemove)
                {
                    materialized.Remove(toRemove);
                }

                _logger.LogInformation(
                    "QualityGate: Hid {Count} fully-blocked items from enumerable for user {User} (policy: {Policy})",
                    enumItemsToRemove.Count, (object)userId, policy.Name);
            }

            result.Value = materialized;
        }
    }

    /// <summary>
    /// Determines if an item should be hidden from listings because all its media sources are blocked.
    /// When MediaSources is populated on the DTO, filters them in-place and returns true if none remain.
    /// When MediaSources is null (listing endpoints that don't request Fields=MediaSources), looks up
    /// the actual sources from the library to make the visibility decision.
    /// </summary>
    private bool ShouldHideItem(BaseItemDto itemDto, QualityPolicy policy, Guid userId)
    {
        if (itemDto.MediaSources?.Any() == true)
        {
            var original = itemDto.MediaSources.ToList();
            itemDto.MediaSources = original
                .Where(s => QualityGateService.IsPathAllowed(policy, s.Path))
                .ToArray();

            _logger.LogDebug(
                "QualityGate: Filtered '{Name}' for user {User} - {Original} to {Filtered} sources",
                itemDto.Name, (object)userId, original.Count, itemDto.MediaSources.Length);

            return itemDto.MediaSources.Length == 0 && original.Count > 0;
        }

        // MediaSources not populated — look up from library to decide visibility
        try
        {
            var baseItem = _libraryManager.GetItemById(itemDto.Id);
            if (baseItem == null)
            {
                return false;
            }

            var sources = _mediaSourceManager.GetStaticMediaSources(baseItem, false);
            if (sources == null || sources.Count == 0)
            {
                return false;
            }

            var allBlocked = !sources.Any(s => QualityGateService.IsPathAllowed(policy, s.Path));
            if (allBlocked)
            {
                _logger.LogDebug(
                    "QualityGate: All {Count} sources blocked for '{Name}' (user {User}, library lookup)",
                    sources.Count, itemDto.Name, (object)userId);
            }

            return allBlocked;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "QualityGate: Error looking up sources for '{Name}', allowing", itemDto.Name);
            return false;
        }
    }

    /// <summary>
    /// Checks if a file path is a configured intro video (policy or default).
    /// Intro videos must always be playable regardless of user policy.
    /// </summary>
    private static bool IsConfiguredIntroPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(config.DefaultIntroVideoPath)
            && path.Equals(config.DefaultIntroVideoPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return config.Policies.Any(p =>
            !string.IsNullOrWhiteSpace(p.IntroVideoPath)
            && path.Equals(p.IntroVideoPath, StringComparison.OrdinalIgnoreCase));
    }

    private static Guid GetUserId(HttpContext httpContext)
    {
        // This filter intercepts Jellyfin's OWN endpoints (PlaybackInfo, /Users/{id}/Items, etc.)
        // where the userId is embedded in query params, route values, or the URL path by Jellyfin itself.
        // Unlike our custom QualityGateController (which binds exclusively to JWT claims),
        // we NEED these fallbacks because Jellyfin's internal API design passes userId this way.
        // The [Authorize] attribute on Jellyfin's controllers already validates the caller is authenticated.

        // Primary: JWT/cookie claims set by Jellyfin auth
        var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)
                 ?? httpContext.User.FindFirst("Jellyfin-UserId");
        if (claim != null && Guid.TryParse(claim.Value, out var userId) && userId != Guid.Empty)
        {
            return userId;
        }

        // Fallback: userId query parameter (used by PlaybackInfo, etc.)
        if (httpContext.Request.Query.TryGetValue("userId", out var queryUserId)
            && Guid.TryParse(queryUserId.FirstOrDefault(), out userId) && userId != Guid.Empty)
        {
            return userId;
        }

        // Fallback: route values (used by /Users/{userId}/Items/...)
        if (httpContext.Request.RouteValues.TryGetValue("userId", out var routeUserId)
            && Guid.TryParse(routeUserId?.ToString(), out userId) && userId != Guid.Empty)
        {
            return userId;
        }

        // Last resort: extract from URL path /Users/{userId}/...
        var path = httpContext.Request.Path.Value ?? string.Empty;
        var usersIdx = path.IndexOf("/Users/", StringComparison.OrdinalIgnoreCase);
        if (usersIdx >= 0)
        {
            var start = usersIdx + 7;
            var end = path.IndexOf('/', start);
            if (end < 0) end = path.Length;
            if (Guid.TryParse(path.AsSpan(start, end - start), out userId) && userId != Guid.Empty)
            {
                return userId;
            }
        }

        return Guid.Empty;
    }
}
