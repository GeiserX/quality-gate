using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// Two-phase MVC filter that forces Jellyfin's transcoding pipeline to build proper
/// TranscodingUrls when fallback-transcode is needed, and removes blocked MediaSources
/// from API responses before serialization.
///
/// Phase 1 (IAsyncResourceFilter): runs BEFORE model binding. For PlaybackInfo requests
/// where fallback is needed, modifies the query string to set enableDirectPlay=false and
/// enableDirectStream=false. Jellyfin's StreamBuilder then chooses Transcode and builds
/// proper TranscodingUrls — simply flipping flags on the result doesn't work because
/// TranscodingUrl remains null when StreamBuilder originally chose DirectPlay.
///
/// Phase 2 (IAsyncResultFilter): runs BEFORE serialization. Filters blocked sources from
/// responses, hides fully-blocked items from listings, and skips filtering for PlaybackInfo
/// requests where the pipeline was already forced into transcode mode.
/// </summary>
public class MediaSourceResultFilter : IAsyncResourceFilter, IAsyncResultFilter
{
    private const string ForcedTranscodeKey = "QG_ForcedTranscode";

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

    /// <summary>
    /// Phase 1: Resource filter — runs BEFORE model binding.
    /// For PlaybackInfo POST requests where all sources are blocked by the user's policy
    /// and FallbackTranscode is enabled, modifies the request body to strip
    /// DirectPlayProfiles from the DeviceProfile. This forces Jellyfin's StreamBuilder
    /// to choose Transcode and build proper TranscodingUrls.
    ///
    /// In Jellyfin 10.11+ the enableDirectPlay/enableDirectStream query parameters are
    /// obsolete and ignored — the DeviceProfile in the POST body is the sole driver of
    /// the direct-play vs transcode decision.
    /// </summary>
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;

        if (path.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase)
            && string.Equals(context.HttpContext.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            await TryForceTranscodeBodyAsync(context.HttpContext, path).ConfigureAwait(false);
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Phase 2: Result filter — runs BEFORE response serialization.
    /// Filters blocked sources, hides fully-blocked items, and skips filtering for
    /// PlaybackInfo requests where the pipeline was already forced into transcode mode.
    /// </summary>
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        var isPlaybackInfo = path.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase);
        var hasItems = path.Contains("/Items/", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("/Items", StringComparison.OrdinalIgnoreCase);
        var isRelevant = isPlaybackInfo
                      || (hasItems
                          && path.Contains("/Users/", StringComparison.OrdinalIgnoreCase)
                          && !path.EndsWith("/Intros", StringComparison.OrdinalIgnoreCase));

        var forcedTranscode = context.HttpContext.Items.ContainsKey(ForcedTranscodeKey);

        if (isRelevant)
        {
            _logger.LogInformation(
                "QualityGate: ResultFilter fired for {Path}, forcedTranscode: {Forced}",
                path, forcedTranscode);
        }

        if (isRelevant && context.Result is ObjectResult { Value: not null } objectResult)
        {
            var userId = GetUserId(context.HttpContext);
            if (userId != Guid.Empty)
            {
                var policy = QualityGateService.GetUserPolicy(userId);
                if (policy != null)
                {
                    if (forcedTranscode && objectResult.Value is PlaybackInfoResponse)
                    {
                        // The pipeline already built proper TranscodingUrls because we
                        // disabled DirectPlay/DirectStream in the query string (Phase 1).
                        // No filtering needed — all sources are set to transcode.
                        _logger.LogInformation(
                            "QualityGate: Fallback transcode via pipeline for user {User} (policy: {Policy})",
                            (object)userId, policy.Name);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "QualityGate: Applying policy {Policy} for user {User}",
                            policy.Name, (object)userId);
                        FilterResult(objectResult, policy, userId);
                    }
                }
            }
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Checks if the current PlaybackInfo POST request needs fallback transcoding, and if so,
    /// modifies the request body to strip DirectPlayProfiles from the DeviceProfile.
    /// This runs BEFORE model binding, so Jellyfin's StreamBuilder receives a DeviceProfile
    /// with no DirectPlay options and is forced to choose Transcode, building proper
    /// TranscodingUrls that the client needs to initiate server-side transcoding.
    ///
    /// In Jellyfin 10.11+ the enableDirectPlay query parameter is obsolete and ignored —
    /// the DeviceProfile in the POST body is the sole driver of the playback decision.
    /// </summary>
    private async Task TryForceTranscodeBodyAsync(HttpContext httpContext, string path)
    {
        try
        {
            var userId = GetUserId(httpContext);
            if (userId == Guid.Empty)
            {
                return;
            }

            var policy = QualityGateService.GetUserPolicy(userId);
            if (policy == null || !policy.FallbackTranscode
                || ReferenceEquals(policy, QualityGateService.DenyAllPolicy))
            {
                return;
            }

            var itemId = ExtractItemIdFromPath(path);
            if (itemId == Guid.Empty)
            {
                return;
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item == null)
            {
                return;
            }

            var sources = _mediaSourceManager.GetStaticMediaSources(item, false);
            if (sources == null || sources.Count == 0)
            {
                return;
            }

            // If any source passes the policy, normal filtering is enough — no fallback needed
            if (sources.Any(s => QualityGateService.IsSourcePlayable(policy, s.Path)))
            {
                return;
            }

            // All sources blocked + FallbackTranscode ON → strip DirectPlayProfiles from the
            // DeviceProfile in the POST body so StreamBuilder falls through to Transcode.
            httpContext.Request.EnableBuffering();
            httpContext.Request.Body.Position = 0;

            using var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);

            JsonNode? node = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                node = JsonNode.Parse(body);
            }

            node ??= new JsonObject();

            if (node is JsonObject obj)
            {
                if (obj["DeviceProfile"] is JsonObject profile)
                {
                    // Strip DirectPlayProfiles so nothing matches for DirectPlay
                    profile["DirectPlayProfiles"] = new JsonArray();

                    // Ensure at least one TranscodingProfile exists for the fallback
                    if (profile["TranscodingProfiles"] is not JsonArray { Count: > 0 })
                    {
                        profile["TranscodingProfiles"] = BuildDefaultTranscodingProfiles();
                    }
                }
                else
                {
                    // No DeviceProfile in body — inject a minimal one that only allows transcode
                    obj["DeviceProfile"] = new JsonObject
                    {
                        ["DirectPlayProfiles"] = new JsonArray(),
                        ["TranscodingProfiles"] = BuildDefaultTranscodingProfiles(),
                        ["ContainerProfiles"] = new JsonArray(),
                        ["CodecProfiles"] = new JsonArray(),
                        ["SubtitleProfiles"] = new JsonArray(),
                    };
                }
            }

            var modifiedBody = node.ToJsonString();
            var bytes = Encoding.UTF8.GetBytes(modifiedBody);
            httpContext.Request.Body = new MemoryStream(bytes);
            httpContext.Request.ContentLength = bytes.Length;
            httpContext.Request.ContentType ??= "application/json";
            httpContext.Items[ForcedTranscodeKey] = true;

            _logger.LogInformation(
                "QualityGate: Forcing transcode via DeviceProfile for item {Item} (user {User}, policy {Policy})",
                itemId, (object)userId, policy.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QualityGate: Error in TryForceTranscodeBodyAsync, skipping");
        }
    }

    /// <summary>
    /// Builds a default set of TranscodingProfiles for the fallback DeviceProfile.
    /// Covers HLS video (h264/aac) and audio-only (aac) which are universally supported.
    /// </summary>
    private static JsonArray BuildDefaultTranscodingProfiles()
    {
        return new JsonArray(
            new JsonObject
            {
                ["Container"] = "ts",
                ["Type"] = "Video",
                ["AudioCodec"] = "aac,ac3",
                ["VideoCodec"] = "h264",
                ["Protocol"] = "hls",
            },
            new JsonObject
            {
                ["Container"] = "mp3",
                ["Type"] = "Audio",
                ["AudioCodec"] = "mp3",
                ["Protocol"] = "http",
            });
    }

    /// <summary>
    /// Extracts the item GUID from a URL path like /Items/{guid}/PlaybackInfo.
    /// </summary>
    private static Guid ExtractItemIdFromPath(string path)
    {
        var itemsIdx = path.IndexOf("/Items/", StringComparison.OrdinalIgnoreCase);
        if (itemsIdx < 0)
        {
            return Guid.Empty;
        }

        var start = itemsIdx + 7; // length of "/Items/"
        var end = path.IndexOf('/', start);
        if (end <= start)
        {
            return Guid.Empty;
        }

        return Guid.TryParse(path.AsSpan(start, end - start), out var itemId) ? itemId : Guid.Empty;
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
                    .Where(s => QualityGateService.IsSourcePlayable(policy, s.Path))
                    .ToArray();

                _logger.LogInformation(
                    "QualityGate: Filtered PlaybackInfo for user {User} (policy: {Policy}) - {Original} to {Filtered} sources",
                    (object)userId, policy.Name, original.Count, filtered.Length);
                playbackInfo.MediaSources = filtered;

                if (filtered.Length == 0 && original.Count > 0)
                {
                    // We already know all sources failed IsSourcePlayable above.
                    // Use PolicyAllowsFallback + file-existence check to avoid re-scanning.
                    if (QualityGateService.PolicyAllowsFallback(policy)
                        && original.Any(s => !string.IsNullOrEmpty(s.Path) && File.Exists(s.Path)))
                    {
                        playbackInfo.MediaSources = QualityGateService.ApplyFallbackTranscode(original);
                        _logger.LogInformation(
                            "QualityGate: Fallback transcode for user {User} (policy: {Policy}) — {Count} sources forced to transcode",
                            (object)userId, policy.Name, original.Count);
                    }
                    else
                    {
                        playbackInfo.ErrorCode = MediaBrowser.Model.Dlna.PlaybackErrorCode.NotAllowed;
                        _logger.LogInformation(
                            "QualityGate: All sources blocked for user {User} (policy: {Policy}) — returning NotAllowed",
                            (object)userId, policy.Name);
                    }
                }

                break;
            }

            case BaseItemDto itemDto when itemDto.MediaSources?.Any() == true:
            {
                var original = itemDto.MediaSources.ToList();
                var filtered = original
                    .Where(s => QualityGateService.IsSourcePlayable(policy, s.Path))
                    .ToArray();

                _logger.LogInformation(
                    "QualityGate: Filtered item sources for user {User} (policy: {Policy}) - {Original} to {Filtered} sources",
                    (object)userId, policy.Name, original.Count, filtered.Length);

                if (filtered.Length == 0 && original.Count > 0
                    && QualityGateService.PolicyAllowsFallback(policy)
                    && original.Any(s => !string.IsNullOrEmpty(s.Path) && File.Exists(s.Path)))
                {
                    itemDto.MediaSources = QualityGateService.ApplyFallbackTranscode(original);
                    _logger.LogInformation(
                        "QualityGate: Fallback transcode for item '{Name}' user {User} (policy: {Policy})",
                        itemDto.Name, (object)userId, policy.Name);
                }
                else
                {
                    itemDto.MediaSources = filtered;
                }

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
                .Where(s => QualityGateService.IsSourcePlayable(policy, s.Path))
                .ToArray();

            _logger.LogDebug(
                "QualityGate: Filtered '{Name}' for user {User} - {Original} to {Filtered} sources",
                itemDto.Name, (object)userId, original.Count, itemDto.MediaSources.Length);

            if (itemDto.MediaSources.Length == 0 && original.Count > 0)
            {
                if (QualityGateService.PolicyAllowsFallback(policy)
                    && original.Any(s => !string.IsNullOrEmpty(s.Path) && File.Exists(s.Path)))
                {
                    itemDto.MediaSources = QualityGateService.ApplyFallbackTranscode(original);
                    return false;
                }

                return true;
            }

            return false;
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

            var allBlocked = !sources.Any(s => QualityGateService.IsSourcePlayable(policy, s.Path));
            if (allBlocked)
            {
                if (QualityGateService.PolicyAllowsFallback(policy)
                    && sources.Any(s => !string.IsNullOrEmpty(s.Path) && File.Exists(s.Path)))
                {
                    _logger.LogDebug(
                        "QualityGate: Fallback transcode — not hiding '{Name}' (user {User}, library lookup)",
                        itemDto.Name, (object)userId);
                    return false;
                }

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
