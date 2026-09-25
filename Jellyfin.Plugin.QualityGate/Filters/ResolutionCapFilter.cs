using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.QualityGate.Configuration;
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
/// Enforces a policy's resolution cap against the media's actual height.
///
/// The height comes from the item's video <c>MediaStream</c>, never from its filename.
/// A filename is only a label an operator controls; on a library whose lower-quality tree
/// symlinks to originals under their original names, the label is absent from the path the
/// server actually stores, so filename patterns cannot express "no more than 720p".
///
/// Two paths deliver video, so this filter covers both:
///
/// Negotiation — POST /Items/{id}/PlaybackInfo. Phase 1 adds a required
/// <c>Height &lt;= cap</c> video condition to the DeviceProfile in the request body, before
/// model binding. Jellyfin's StreamBuilder turns a failed Height condition into
/// <c>TranscodeReason.VideoResolutionNotSupported</c>, which rules out direct play, and maps
/// the same <c>LessThanEqual</c> condition onto the transcode's own MaxHeight — so an
/// over-cap source comes back as a capped transcode rather than the original. Phase 2 then
/// guarantees the response itself: an over-cap source is dropped when a source within the cap
/// exists (the 720p sibling is offered instead), and when every source is over the cap they
/// are kept but marked transcode-only.
///
/// Direct delivery — the routes that hand back bytes without asking anything: video and HLS
/// under /Videos, the /Audio stream routes (which serve a video item's original file with no
/// item-type check), and /Items/{id}/File and /Items/{id}/Download, which return the original
/// outright. Negotiation cannot gate any of these: they are separate MVC actions a client can
/// call directly, and the GET form of PlaybackInfo applies no limits at all. Phase 1 refuses
/// them with 403 when the item is over the cap and the request asks for the bytes as they are
/// or for a transcode that is not held to the cap. A properly negotiated request carries
/// <c>MaxHeight</c> at or below the cap and passes untouched.
///
/// One route is deliberately NOT covered: the legacy HLS segment route
/// /Videos/{itemId}/hls/{playlistId}/{segmentId}.{container}. Its <c>itemId</c> is declared but
/// never read — the file is located purely by <c>segmentId</c>, an MD5 of media path, user
/// agent, device id and play session id — so a request cannot be mapped back to an item and
/// this filter has nothing to check. It can only return segments that already exist in the
/// transcode folder, which for a restricted user are segments this filter already capped.
/// docs/how-it-works.md records it as an accepted, known bypass and what closing it would take.
///
/// The filter fails OPEN everywhere except one place. An unreadable body, an item it cannot
/// resolve or an unexpected exception is logged and allowed, because a plugin defect must not
/// take playback away from everyone. The one place it does not is a delivery request from a
/// user the filter has already established is capped: there it fails CLOSED. By then the only
/// question left is how tall the media is, so a throw is a defect in this plugin rather than
/// something the library said, and allowing the request would hand over exactly the bytes the
/// cap exists to withhold.
/// </summary>
public class ResolutionCapFilter : IAsyncResourceFilter, IAsyncResultFilter
{
    /// <summary>
    /// The claim Jellyfin 12 issues for the authenticated user, holding a "N"-format Guid.
    /// See CustomAuthenticationHandler: Jellyfin 12 does NOT issue ClaimTypes.NameIdentifier.
    /// </summary>
    private const string JellyfinUserIdClaim = "Jellyfin-UserId";

    /// <summary>Index of the media source id inside the legacy <c>params</c> blob.</summary>
    private const int ParamsMediaSourceIdIndex = 2;

    /// <summary>Index of the static flag inside the legacy <c>params</c> blob.</summary>
    private const int ParamsStaticIndex = 3;

    /// <summary>Index of the max height inside the legacy <c>params</c> blob.</summary>
    private const int ParamsMaxHeightIndex = 13;

    private readonly ILogger<ResolutionCapFilter> _logger;
    private readonly IMediaSourceManager _mediaSourceManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResolutionCapFilter"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    public ResolutionCapFilter(
        ILogger<ResolutionCapFilter> logger,
        IMediaSourceManager mediaSourceManager)
    {
        _logger = logger;
        _mediaSourceManager = mediaSourceManager;
    }

    /// <summary>
    /// Phase 1 — runs before model binding. Refuses over-cap delivery, and caps the
    /// DeviceProfile on PlaybackInfo so the negotiated result is a capped transcode.
    /// </summary>
    /// <param name="context">The resource executing context.</param>
    /// <param name="next">The next delegate in the pipeline.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var refuse = false;

        try
        {
            // Match the path before touching claims or config: this filter is global, so the
            // overwhelming majority of requests are neither delivery nor negotiation and must
            // cost nothing more than two string checks.
            var path = context.HttpContext.Request.Path.Value ?? string.Empty;
            var isDelivery = IsMediaDeliveryRequest(path);

            if (isDelivery || IsPlaybackInfoPost(context.HttpContext, path))
            {
                var userId = GetUserId(context.HttpContext);
                var policy = GetCappedPolicy(userId);

                if (policy != null)
                {
                    if (isDelivery)
                    {
                        try
                        {
                            refuse = ShouldRefuseDelivery(context.HttpContext, policy, userId, path);
                        }
                        catch (Exception ex)
                        {
                            // Fail closed: a capped user is asking for bytes and the gate could
                            // not finish deciding. That is a defect here, not a property of the
                            // media, and letting it through delivers the original.
                            _logger.LogError(
                                ex,
                                "QualityGate: the {Cap}p cap could not be evaluated for {Path} — refusing the request",
                                policy.MaxHeight,
                                path);
                            refuse = true;
                        }
                    }
                    else
                    {
                        await CapDeviceProfileAsync(context.HttpContext, policy, userId).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Fail open: never let a defect here stop playback for everyone.
            _logger.LogError(
                ex,
                "QualityGate: resolution cap could not be evaluated for {Path} — allowing the request",
                context.HttpContext.Request.Path.Value);
        }

        if (refuse)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Phase 2 — runs before serialization. Guarantees that no source above the cap is
    /// offered to a restricted user as something they can play as-is.
    /// </summary>
    /// <param name="context">The result executing context.</param>
    /// <param name="next">The next delegate in the pipeline.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        try
        {
            if (context.Result is ObjectResult { Value: PlaybackInfoResponse response })
            {
                var userId = GetUserId(context.HttpContext);
                var policy = GetCappedPolicy(userId);
                if (policy != null)
                {
                    CapPlaybackInfo(response, policy, userId);
                }
                else
                {
                    // No cap to apply, but these are exactly the users who were being handed
                    // the encoded sibling ahead of the original they are entitled to.
                    response.MediaSources = QualityGateService.OrderBestFirst(response.MediaSources);
                }
            }
            else if (context.Result is ObjectResult { Value: not null } itemResult)
            {
                // The same order has to hold on the item itself. That list is what fills the
                // version picker, and a client that plays the entry it shows first would walk
                // straight past the ordering applied above.
                OrderItemSources(itemResult.Value, GetCappedPolicy(GetUserId(context.HttpContext)));
            }
        }
        catch (Exception ex)
        {
            // Fail open: leave the response exactly as Jellyfin built it.
            _logger.LogError(
                ex,
                "QualityGate: resolution cap could not be applied to the PlaybackInfo response for {Path}",
                context.HttpContext.Request.Path.Value);
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the same best-first order to the media sources carried on an item response.
    /// </summary>
    /// <param name="value">The result value, which may be a single item or a page of them.</param>
    /// <param name="policy">The viewer's policy, or null when they are unrestricted.</param>
    internal static void OrderItemSources(object? value, QualityPolicy? policy)
    {
        switch (value)
        {
            case BaseItemDto item:
                OrderOne(item);
                break;

            case QueryResult<BaseItemDto> page when page.Items != null:
                foreach (var item in page.Items)
                {
                    OrderOne(item);
                }

                break;

            case IEnumerable<BaseItemDto> items:
                foreach (var item in items)
                {
                    OrderOne(item);
                }

                break;
        }

        void OrderOne(BaseItemDto? item)
        {
            // One source cannot be out of order, and re-ordering every item in a library page
            // that carries no sources would be pure overhead.
            if (item?.MediaSources is { Length: > 1 })
            {
                item.MediaSources = QualityGateService.OrderForPolicy(policy, item.MediaSources);
            }
        }
    }

    /// <summary>
    /// Removes sources above the cap when a source within the cap exists, and otherwise
    /// keeps every source but forbids playing them as they are.
    /// </summary>
    /// <param name="response">The playback info response to cap.</param>
    /// <param name="policy">The user's policy.</param>
    /// <param name="userId">The user id, for logging.</param>
    internal void CapPlaybackInfo(PlaybackInfoResponse response, QualityPolicy policy, Guid userId)
    {
        var sources = response.MediaSources;
        if (sources == null || sources.Count == 0)
        {
            return;
        }

        // The same predicate the encode priority list uses, so what playback transcodes and what
        // the list asks an encoder to fix can never disagree.
        var heights = sources.Select(QualityGateService.GetVideoHeight).ToArray();
        if (!QualityGateService.IsCapGap(heights, policy.MaxHeight))
        {
            var withinCap = sources.Where(s => !QualityGateService.ExceedsHeightCap(policy, s)).ToArray();
            if (withinCap.Length == sources.Count)
            {
                // Nothing is over the cap, so nothing may be removed — but the best of them
                // still goes first, because clients play MediaSources[0].
                response.MediaSources = QualityGateService.OrderBestFirst(sources);
                return;
            }

            _logger.LogInformation(
                "QualityGate: capped PlaybackInfo at {Cap}p for user {User} (policy: {Policy}) — offering {Kept} of {Total} sources",
                policy.MaxHeight, (object)userId, policy.Name, withinCap.Length, sources.Count);
            response.MediaSources = QualityGateService.OrderBestFirst(withinCap);
            return;
        }

        // Every source is over the cap. Blocking would take the item away entirely, so keep
        // the sources but strip direct play/stream: Jellyfin must transcode, and the required
        // Height condition added during negotiation holds that transcode to the cap.
        _logger.LogInformation(
            "QualityGate: every source is above the {Cap}p cap for user {User} (policy: {Policy}) — forcing a capped transcode of {Total} sources",
            policy.MaxHeight, (object)userId, policy.Name, sources.Count);
        // Cheapest first: every one of these gets transcoded down to the same cap, so the
        // smallest source produces the same picture for the least CPU.
        response.MediaSources = QualityGateService.OrderCheapestFirst(
            QualityGateService.ApplyFallbackTranscode(sources));
    }

    /// <summary>
    /// Decides whether a direct delivery request must be refused.
    /// </summary>
    /// <param name="httpContext">The request context.</param>
    /// <param name="policy">The user's policy.</param>
    /// <param name="userId">The user id, for logging.</param>
    /// <param name="path">The request path.</param>
    /// <returns>True when the request must be answered with 403.</returns>
    internal bool ShouldRefuseDelivery(HttpContext httpContext, QualityPolicy policy, Guid userId, string path)
    {
        var itemIds = ResolveDeliveryItemIds(httpContext, path);
        if (itemIds.Count == 0)
        {
            // Cannot tell what is being fetched — fail open rather than guess. Every gated
            // route binds a Guid itemId, so a request that gets here carries no id MVC could
            // have bound either, and the action will refuse it before it delivers anything.
            _logger.LogWarning(
                "QualityGate: could not resolve an item id for {Path} — allowing the request", path);
            return false;
        }

        // Measure every candidate and judge the request by the tallest of them: which one the
        // action ends up serving is decided after this filter has run.
        var itemId = itemIds[0];
        int? height = null;
        foreach (var candidate in itemIds)
        {
            var candidateHeight = GetItemVideoHeight(candidate);
            if (candidateHeight.HasValue && (!height.HasValue || candidateHeight.Value > height.Value))
            {
                height = candidateHeight;
                itemId = candidate;
            }
        }

        if (!QualityGateService.ExceedsHeightCap(policy, height))
        {
            if (!height.HasValue)
            {
                _logger.LogWarning(
                    "QualityGate: item {Item} reports no video height, so the {Cap}p cap cannot be applied to {Path} — allowing the request. Refresh the item's metadata to restore enforcement",
                    (object)itemId, policy.MaxHeight, path);
            }

            return false;
        }

        if (IsOriginalFileRequest(path))
        {
            _logger.LogWarning(
                "QualityGate: refused the original file of {Height}p item {Item} to user {User} (policy: {Policy}, cap: {Cap}p, path: {Path})",
                height, (object)itemId, (object)userId, policy.Name, policy.MaxHeight, path);
            return true;
        }

        var intent = ReadStreamingIntent(httpContext.Request.Query);

        if (intent.IsStatic)
        {
            _logger.LogWarning(
                "QualityGate: refused a byte-for-byte copy of {Height}p item {Item} to user {User} (policy: {Policy}, cap: {Cap}p)",
                height, (object)itemId, (object)userId, policy.Name, policy.MaxHeight);
            return true;
        }

        if (intent.RequestedHeight.HasValue && intent.RequestedHeight.Value <= policy.MaxHeight)
        {
            // A transcode that is already held to the cap. This is what a properly negotiated
            // client asks for, so it must keep working.
            return false;
        }

        _logger.LogWarning(
            "QualityGate: refused an uncapped stream of {Height}p item {Item} to user {User} (policy: {Policy}, cap: {Cap}p, requested height: {Requested})",
            height, (object)itemId, (object)userId, policy.Name, policy.MaxHeight, intent.RequestedHeight);
        return true;
    }

    /// <summary>
    /// Adds a required <c>Height &lt;= cap</c> video condition to the DeviceProfile carried in
    /// the PlaybackInfo request body, before model binding reads it.
    ///
    /// Only Height is constrained, never Width: a 2.39:1 film at 720p is 1720 pixels wide, so
    /// a width condition derived from 16:9 would force a transcode of media already within
    /// the cap.
    ///
    /// The condition is marked required so that an item with no probed height fails it and is
    /// transcoded at the cap. Jellyfin treats an unknown value as satisfying a condition that
    /// is not required, which would otherwise let unprobed media direct play at full size.
    /// </summary>
    private async Task CapDeviceProfileAsync(HttpContext httpContext, QualityPolicy policy, Guid userId)
    {
        httpContext.Request.EnableBuffering();
        httpContext.Request.Body.Position = 0;

        string body;
        using (var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        httpContext.Request.Body.Position = 0;

        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        if (JsonNode.Parse(body) is not JsonObject root)
        {
            return;
        }

        // Only cap a profile the client actually sent. Inventing one would hand StreamBuilder
        // a device profile with no direct play and no transcoding profiles, which plays nothing.
        if (root["DeviceProfile"] is not JsonObject profile)
        {
            return;
        }

        if (profile["CodecProfiles"] is not JsonArray codecProfiles)
        {
            codecProfiles = new JsonArray();
            profile["CodecProfiles"] = codecProfiles;
        }

        codecProfiles.Add(new JsonObject
        {
            ["Type"] = "Video",
            ["Conditions"] = new JsonArray(
                new JsonObject
                {
                    ["Condition"] = "LessThanEqual",
                    ["Property"] = "Height",
                    ["Value"] = policy.MaxHeight.ToString(CultureInfo.InvariantCulture),
                    ["IsRequired"] = true,
                }),
        });

        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        httpContext.Request.Body = new MemoryStream(bytes);
        httpContext.Request.ContentLength = bytes.Length;

        _logger.LogInformation(
            "QualityGate: capped the negotiated device profile at {Cap}p for user {User} (policy: {Policy})",
            policy.MaxHeight, (object)userId, policy.Name);
    }

    /// <summary>
    /// Gets the user's policy, but only when it actually caps resolution.
    /// Returns null for an unrestricted user and for a policy with no height cap, so both
    /// see the request exactly as Jellyfin would have handled it.
    /// </summary>
    private static QualityPolicy? GetCappedPolicy(Guid userId)
    {
        // An empty id is Jellyfin's API-key or anonymous caller, not a user who happens to have no
        // assignment, so it is resolved through the opt-in API-key policy rather than the per-user
        // table. Without that it matched no assignment, took no default, and was served uncapped.
        var policy = userId == Guid.Empty
            ? QualityGateService.GetApiKeyPolicy()
            : QualityGateService.GetUserPolicy(userId);

        return QualityGateService.HasHeightCap(policy) ? policy : null;
    }

    /// <summary>
    /// Gets the height of the tallest video stream recorded for an item.
    /// </summary>
    private int? GetItemVideoHeight(Guid itemId)
    {
        return QualityGateService.GetVideoHeight(_mediaSourceManager.GetMediaStreams(itemId));
    }

    /// <summary>
    /// Identifies every item whose bytes a delivery request could be asking for.
    ///
    /// On /Items/{itemId}/File and /Items/{itemId}/Download the route id is the only answer.
    /// Those actions take no media source parameter at all: they return the file of the item
    /// named in the route, whatever else the query says. Measuring a caller-supplied
    /// <c>mediaSourceId</c> there would let a capped user name a 480p sibling and be handed
    /// the 4K original.
    ///
    /// Every other delivery route can be pointed at a specific version, and which identifier
    /// the action settles on is decided after this filter has run — the legacy <c>params</c>
    /// blob is applied inside the action and overrides the bound media source id. So all the
    /// candidates are measured and the tallest one decides, which holds the cap whichever the
    /// action picks.
    /// </summary>
    /// <param name="httpContext">The request context.</param>
    /// <param name="path">The request path.</param>
    /// <returns>The distinct item ids the request could resolve to, in the order they were found.</returns>
    private static IReadOnlyList<Guid> ResolveDeliveryItemIds(HttpContext httpContext, string path)
    {
        var routeItemId = ResolveRouteItemId(httpContext, path);

        if (IsOriginalFileRequest(path))
        {
            return routeItemId == Guid.Empty ? Array.Empty<Guid>() : new[] { routeItemId };
        }

        var itemIds = new List<Guid>();
        var query = httpContext.Request.Query;

        if (TryGetGuid(GetParam(ReadParams(query), ParamsMediaSourceIdIndex), out var paramSourceId))
        {
            itemIds.Add(paramSourceId);
        }

        if (query.TryGetValue("mediaSourceId", out var mediaSourceId)
            && TryGetGuid(mediaSourceId.FirstOrDefault(), out var sourceId)
            && !itemIds.Contains(sourceId))
        {
            itemIds.Add(sourceId);
        }

        if (routeItemId != Guid.Empty && !itemIds.Contains(routeItemId))
        {
            itemIds.Add(routeItemId);
        }

        return itemIds;
    }

    /// <summary>
    /// Reads the item id the route itself carries, from the bound route values when they are
    /// available and otherwise from the path.
    /// </summary>
    /// <param name="httpContext">The request context.</param>
    /// <param name="path">The request path.</param>
    /// <returns>The route's item id, or <see cref="Guid.Empty"/> when it carries none.</returns>
    private static Guid ResolveRouteItemId(HttpContext httpContext, string path)
    {
        if (httpContext.Request.RouteValues.TryGetValue("itemId", out var routeItemId)
            && TryGetGuid(routeItemId?.ToString(), out var itemId))
        {
            return itemId;
        }

        return ExtractItemIdFromPath(path);
    }

    private static bool TryGetGuid(string? value, out Guid result)
    {
        return Guid.TryParse(value, out result) && result != Guid.Empty;
    }

    /// <summary>
    /// Extracts the item GUID that follows /Videos/, /Audio/ or /Items/ in a request path.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <returns>The item id, or <see cref="Guid.Empty"/> when the path carries none.</returns>
    internal static Guid ExtractItemIdFromPath(string path)
    {
        foreach (var prefix in new[] { "/Videos/", "/Audio/", "/Items/" })
        {
            var idx = path.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            var start = idx + prefix.Length;
            var end = path.IndexOf('/', start);
            if (end < 0)
            {
                end = path.Length;
            }

            if (end > start && Guid.TryParse(path.AsSpan(start, end - start), out var itemId))
            {
                return itemId;
            }
        }

        return Guid.Empty;
    }

    /// <summary>
    /// Checks whether a path is one of the routes that hand back media bytes or a playlist.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <returns>True when the route delivers media rather than metadata.</returns>
    internal static bool IsMediaDeliveryRequest(string path)
    {
        if (IsOriginalFileRequest(path))
        {
            return true;
        }

        // /Audio is included because its stream routes never check the item type: asking for a
        // video item through them returns that video's original file.
        var isMediaRoute = path.Contains("/Videos/", StringComparison.OrdinalIgnoreCase)
                        || path.Contains("/Audio/", StringComparison.OrdinalIgnoreCase);

        return isMediaRoute
            && (path.EndsWith("/stream", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/stream.", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/universal", StringComparison.OrdinalIgnoreCase)
                || path.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/hls1/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks whether a path returns the untouched original file, where no transcode
    /// parameter can bring the delivered resolution down.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <returns>True for the routes that always return the original.</returns>
    internal static bool IsOriginalFileRequest(string path)
    {
        return path.Contains("/Items/", StringComparison.OrdinalIgnoreCase)
            && (path.EndsWith("/File", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/Download", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks whether the request is the POST form of PlaybackInfo, the only form that
    /// carries a DeviceProfile.
    /// </summary>
    /// <param name="httpContext">The request context.</param>
    /// <param name="path">The request path.</param>
    /// <returns>True for a PlaybackInfo POST.</returns>
    internal static bool IsPlaybackInfoPost(HttpContext httpContext, string path)
    {
        return path.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase)
            && string.Equals(httpContext.Request.Method, "POST", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads what a streaming request is actually going to be served, accounting for the
    /// legacy semicolon-separated <c>params</c> blob.
    ///
    /// Jellyfin binds the query string first and then, inside the action, overwrites the bound
    /// values from <c>params</c> by position — index 3 sets the static flag and index 13 sets
    /// the max height. A gate that only read the named query parameters would be walked past
    /// by <c>?params=;;;true</c>.
    /// </summary>
    /// <param name="query">The request query.</param>
    /// <returns>The static flag and requested output height that the action will use.</returns>
    internal static (bool IsStatic, int? RequestedHeight) ReadStreamingIntent(IQueryCollection query)
    {
        var isStatic = ReadBool(query, "static");
        var requestedHeight = ReadInt(query, "maxHeight") ?? ReadInt(query, "height");

        var paramValues = ReadParams(query);

        var staticParam = GetParam(paramValues, ParamsStaticIndex);
        if (!string.IsNullOrWhiteSpace(staticParam))
        {
            isStatic = string.Equals("true", staticParam, StringComparison.OrdinalIgnoreCase);
        }

        // Jellyfin assigns params[13] straight to MaxHeight, and treats 0 (or any non-positive
        // value) as NO maximum. Ignoring such a value would leave a named maxHeight=720 standing
        // while the server actually applies no cap, so the request would look negotiated and
        // sail past. A valid non-positive positional value therefore CLEARS the requested height
        // rather than being skipped, and the item's real height is what gets enforced.
        var heightParam = GetParam(paramValues, ParamsMaxHeightIndex);
        if (!string.IsNullOrWhiteSpace(heightParam)
            && int.TryParse(heightParam, NumberStyles.Integer, CultureInfo.InvariantCulture, out var paramHeight))
        {
            requestedHeight = paramHeight > 0 ? paramHeight : null;
        }

        return (isStatic, requestedHeight);
    }

    private static string[]? ReadParams(IQueryCollection query)
    {
        if (!query.TryGetValue("params", out var values))
        {
            return null;
        }

        var raw = values.FirstOrDefault();
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Split(';');
    }

    private static string? GetParam(string[]? paramValues, int index)
    {
        return paramValues != null && index < paramValues.Length ? paramValues[index] : null;
    }

    private static bool ReadBool(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var values))
        {
            return false;
        }

        var value = values.FirstOrDefault();
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.Ordinal);
    }

    private static int? ReadInt(IQueryCollection query, string key)
    {
        if (query.TryGetValue(key, out var values)
            && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    /// Resolves the authenticated caller.
    /// Jellyfin 12 issues "Jellyfin-UserId" holding a "N"-format Guid and does not issue
    /// ClaimTypes.NameIdentifier; the NameIdentifier lookup keeps 10.x behaviour intact.
    /// </summary>
    /// <param name="httpContext">The request context.</param>
    /// <returns>The caller's user id, or <see cref="Guid.Empty"/> when there is none.</returns>
    internal static Guid GetUserId(HttpContext httpContext)
    {
        var claim = httpContext.User.FindFirst(JellyfinUserIdClaim)
                 ?? httpContext.User.FindFirst(ClaimTypes.NameIdentifier);

        return claim != null && Guid.TryParse(claim.Value, out var userId) ? userId : Guid.Empty;
    }
}
