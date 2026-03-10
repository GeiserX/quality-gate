using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.QualityGate.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QualityGate.Middleware;

/// <summary>
/// Middleware that filters media sources from API responses based on user policies.
/// </summary>
public class QualityGateMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<QualityGateMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="QualityGateMiddleware"/> class.
    /// </summary>
    public QualityGateMiddleware(RequestDelegate next, ILogger<QualityGateMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Process the request.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        
        // Only intercept relevant endpoints that return media sources
        var shouldFilter = path.Contains("/Items/", StringComparison.OrdinalIgnoreCase)
                        || path.Contains("/MediaInfo", StringComparison.OrdinalIgnoreCase)
                        || path.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase);

        if (!shouldFilter)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Get user ID
        var userId = GetUserId(context);
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

        // Capture the response body
        var originalBodyStream = context.Response.Body;
        using var newBodyStream = new MemoryStream();
        context.Response.Body = newBodyStream;

        try
        {
            await _next(context).ConfigureAwait(false);

            // Only process JSON responses
            var contentType = context.Response.ContentType ?? string.Empty;
            if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                newBodyStream.Seek(0, SeekOrigin.Begin);
                await newBodyStream.CopyToAsync(originalBodyStream).ConfigureAwait(false);
                return;
            }

            // Read and filter the response
            newBodyStream.Seek(0, SeekOrigin.Begin);
            var responseBody = await new StreamReader(newBodyStream).ReadToEndAsync().ConfigureAwait(false);

            var filteredBody = FilterMediaSourcesInJson(responseBody, policy);

            // Write filtered response
            var filteredBytes = Encoding.UTF8.GetBytes(filteredBody);
            context.Response.ContentLength = filteredBytes.Length;
            await originalBodyStream.WriteAsync(filteredBytes).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBodyStream;
        }
    }

    private Guid GetUserId(HttpContext context)
    {
        try
        {
            // Try claims
            var claim = context.User.FindFirst(ClaimTypes.NameIdentifier)
                     ?? context.User.FindFirst("Jellyfin-UserId");
            
            if (claim != null && Guid.TryParse(claim.Value, out var userId))
            {
                return userId;
            }

            // Try query string
            if (context.Request.Query.TryGetValue("userId", out var userIdStr)
                && Guid.TryParse(userIdStr.FirstOrDefault(), out userId))
            {
                return userId;
            }

            // Try route values
            if (context.Request.RouteValues.TryGetValue("userId", out var routeUserId)
                && Guid.TryParse(routeUserId?.ToString(), out userId))
            {
                return userId;
            }

            return Guid.Empty;
        }
        catch
        {
            return Guid.Empty;
        }
    }

    private string FilterMediaSourcesInJson(string json, Configuration.QualityPolicy policy)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node == null)
            {
                return json;
            }

            var modified = FilterNode(node, policy);
            return modified ? node.ToJsonString() : json;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "QualityGate: Could not parse/filter JSON response");
            return json;
        }
    }

    private bool FilterNode(JsonNode? node, Configuration.QualityPolicy policy)
    {
        if (node == null)
        {
            return false;
        }

        var modified = false;

        if (node is JsonObject obj)
        {
            // Check for MediaSources array
            if (obj.TryGetPropertyValue("MediaSources", out var mediaSources) && mediaSources is JsonArray sourcesArray)
            {
                var toRemove = new System.Collections.Generic.List<JsonNode>();
                
                foreach (var source in sourcesArray)
                {
                    if (source is JsonObject sourceObj 
                        && sourceObj.TryGetPropertyValue("Path", out var pathNode)
                        && pathNode != null)
                    {
                        var path = pathNode.GetValue<string>();
                        if (!QualityGateService.IsPathAllowed(policy, path))
                        {
                            toRemove.Add(source);
                            _logger.LogDebug("QualityGate: Filtering out media source: {Path}", path);
                        }
                    }
                }

                foreach (var item in toRemove)
                {
                    sourcesArray.Remove(item);
                    modified = true;
                }
            }

            // Recursively process all properties
            foreach (var prop in obj.ToList())
            {
                if (prop.Value != null)
                {
                    modified |= FilterNode(prop.Value, policy);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                modified |= FilterNode(item, policy);
            }
        }

        return modified;
    }
}






