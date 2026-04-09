using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSourceResultFilter"/> class.
    /// </summary>
    public MediaSourceResultFilter(ILogger<MediaSourceResultFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        var isRelevant = path.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase)
                      || (path.Contains("/Items/", StringComparison.OrdinalIgnoreCase)
                          && path.Contains("/Users/", StringComparison.OrdinalIgnoreCase));

        if (isRelevant)
        {
            _logger.LogInformation(
                "QualityGate: ResultFilter fired for {Path}, Result type: {ResultType}",
                path, context.Result?.GetType().Name ?? "null");
        }

        if (context.Result is ObjectResult { Value: not null } objectResult)
        {
            if (isRelevant)
            {
                _logger.LogInformation(
                    "QualityGate: ObjectResult value type: {ValueType}",
                    objectResult.Value.GetType().FullName);
            }

            var userId = GetUserId(context.HttpContext);
            if (userId != Guid.Empty)
            {
                var policy = QualityGateService.GetUserPolicy(userId);
                if (policy != null)
                {
                    _logger.LogInformation(
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
                var original = playbackInfo.MediaSources.ToList();
                var filtered = original
                    .Where(s => QualityGateService.IsPathAllowed(policy, s.Path))
                    .ToArray();

                if (filtered.Length > 0 && filtered.Length < original.Count)
                {
                    _logger.LogInformation(
                        "QualityGate: Filtered PlaybackInfo for user {User} (policy: {Policy}) - {Original} to {Filtered} sources",
                        (object)userId, policy.Name, original.Count, filtered.Length);
                    playbackInfo.MediaSources = filtered;
                }
                else if (filtered.Length == 0)
                {
                    _logger.LogWarning(
                        "QualityGate: All sources blocked for user {User} (policy: {Policy}) - keeping originals",
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

                if (filtered.Length > 0 && filtered.Length < original.Count)
                {
                    _logger.LogInformation(
                        "QualityGate: Filtered item sources for user {User} (policy: {Policy}) - {Original} to {Filtered} sources",
                        (object)userId, policy.Name, original.Count, filtered.Length);
                    itemDto.MediaSources = filtered;
                }
                else if (filtered.Length == 0)
                {
                    _logger.LogWarning(
                        "QualityGate: All sources blocked for user {User} (policy: {Policy}) - keeping originals",
                        (object)userId, policy.Name);
                }

                break;
            }
        }
    }

    private static Guid GetUserId(HttpContext httpContext)
    {
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
