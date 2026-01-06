using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.QualityGate.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QualityGate.Api;

/// <summary>
/// Middleware that filters MediaSources from API responses based on user policy.
/// This ensures restricted users don't even SEE blocked versions in the UI.
/// </summary>
public class MediaSourceFilterMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<MediaSourceFilterMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSourceFilterMiddleware"/> class.
    /// </summary>
    public MediaSourceFilterMiddleware(RequestDelegate next, ILogger<MediaSourceFilterMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Invokes the middleware.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        // Only intercept GET requests that might return MediaSources
        if (context.Request.Method != "GET")
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        
        // Intercept: /Users/{userId}/Items/{itemId} and /Items/{itemId}/PlaybackInfo
        bool shouldFilter = path.Contains("/Items/", StringComparison.OrdinalIgnoreCase) &&
                           (path.Contains("/Users/", StringComparison.OrdinalIgnoreCase) || 
                            path.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase));

        if (!shouldFilter)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Get user ID from path or query
        var userId = ExtractUserId(context);
        if (userId == Guid.Empty)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Check if user has a policy
        var policy = QualityGateService.GetUserPolicy(userId);
        if (policy == null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Capture and modify response
        var originalBodyStream = context.Response.Body;
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        await _next(context).ConfigureAwait(false);

        // Only filter successful JSON responses
        if (context.Response.StatusCode == 200 && 
            (context.Response.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            try
            {
                responseBody.Seek(0, SeekOrigin.Begin);
                var responseText = await new StreamReader(responseBody).ReadToEndAsync().ConfigureAwait(false);
                
                var filteredResponse = FilterMediaSources(responseText, policy, userId);
                
                var modifiedBytes = Encoding.UTF8.GetBytes(filteredResponse);
                context.Response.ContentLength = modifiedBytes.Length;
                context.Response.Body = originalBodyStream;
                await context.Response.Body.WriteAsync(modifiedBytes).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "QualityGate: Error filtering response, passing through");
            }
        }

        // Pass through unmodified
        responseBody.Seek(0, SeekOrigin.Begin);
        context.Response.Body = originalBodyStream;
        await responseBody.CopyToAsync(context.Response.Body).ConfigureAwait(false);
    }

    private static Guid ExtractUserId(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        
        // Try /Users/{userId}/...
        var usersIndex = path.IndexOf("/Users/", StringComparison.OrdinalIgnoreCase);
        if (usersIndex >= 0)
        {
            var start = usersIndex + 7;
            var end = path.IndexOf('/', start);
            if (end < 0) end = path.Length;
            var userIdStr = path.Substring(start, end - start);
            if (Guid.TryParse(userIdStr, out var userId))
            {
                return userId;
            }
        }

        // Try query parameter
        if (context.Request.Query.TryGetValue("userId", out var queryUserId))
        {
            if (Guid.TryParse(queryUserId.FirstOrDefault(), out var userId))
            {
                return userId;
            }
        }

        return Guid.Empty;
    }

    private string FilterMediaSources(string json, Configuration.QualityPolicy policy, Guid userId)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node == null) return json;

            bool modified = false;

            // Filter MediaSources array if present
            if (node is JsonObject obj && obj.ContainsKey("MediaSources"))
            {
                var sources = obj["MediaSources"]?.AsArray();
                if (sources != null)
                {
                    var filtered = new JsonArray();
                    foreach (var source in sources)
                    {
                        var path = source?["Path"]?.GetValue<string>();
                        if (path == null || QualityGateService.IsPathAllowed(policy, path))
                        {
                            filtered.Add(source?.DeepClone());
                        }
                    }

                    if (filtered.Count != sources.Count)
                    {
                        obj["MediaSources"] = filtered;
                        modified = true;
                        _logger.LogInformation(
                            "QualityGate: Filtered MediaSources for user {UserId} - {Original} -> {Filtered}",
                            userId, sources.Count, filtered.Count);
                    }
                }
            }

            return modified ? node.ToJsonString() : json;
        }
        catch
        {
            return json;
        }
    }
}

