using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.QualityGate.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QualityGate.Api;

/// <summary>
/// Middleware that filters PlaybackInfo responses to only include allowed media sources.
/// This prevents users from even seeing versions they're not allowed to play.
/// </summary>
public class QualityGateMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<QualityGateMiddleware> _logger;

    public QualityGateMiddleware(RequestDelegate next, ILogger<QualityGateMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only intercept PlaybackInfo requests
        if (!context.Request.Path.Value?.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) ?? true)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Get user ID from the request
        var userId = GetUserIdFromRequest(context);
        if (userId == Guid.Empty)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Check if user has a policy
        var policy = QualityGateService.GetUserPolicy(userId);
        if (policy == null)
        {
            // No restrictions, pass through
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Capture the response
        var originalBodyStream = context.Response.Body;
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        await _next(context).ConfigureAwait(false);

        // Read and modify the response
        responseBody.Seek(0, SeekOrigin.Begin);
        var responseText = await new StreamReader(responseBody).ReadToEndAsync().ConfigureAwait(false);

        if (context.Response.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) ?? false)
        {
            try
            {
                var modifiedResponse = FilterMediaSources(responseText, policy, userId);
                
                var modifiedBytes = Encoding.UTF8.GetBytes(modifiedResponse);
                context.Response.ContentLength = modifiedBytes.Length;
                
                await originalBodyStream.WriteAsync(modifiedBytes).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QualityGate: Error filtering PlaybackInfo response");
                responseBody.Seek(0, SeekOrigin.Begin);
                await responseBody.CopyToAsync(originalBodyStream).ConfigureAwait(false);
            }
        }
        else
        {
            responseBody.Seek(0, SeekOrigin.Begin);
            await responseBody.CopyToAsync(originalBodyStream).ConfigureAwait(false);
        }

        context.Response.Body = originalBodyStream;
    }

    private Guid GetUserIdFromRequest(HttpContext context)
    {
        // Try to get from query string
        if (context.Request.Query.TryGetValue("userId", out var userIdStr))
        {
            if (Guid.TryParse(userIdStr.FirstOrDefault(), out var userId))
            {
                return userId;
            }
        }

        // Try to get from claims
        var userIdClaim = context.User?.FindFirst("Jellyfin-UserId")?.Value;
        if (!string.IsNullOrEmpty(userIdClaim) && Guid.TryParse(userIdClaim, out var claimUserId))
        {
            return claimUserId;
        }

        return Guid.Empty;
    }

    private string FilterMediaSources(string responseJson, Configuration.QualityPolicy policy, Guid userId)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("MediaSources", out var mediaSources))
        {
            return responseJson;
        }

        var filteredSources = mediaSources.EnumerateArray()
            .Where(source =>
            {
                if (!source.TryGetProperty("Path", out var pathElement))
                {
                    return true;
                }
                var path = pathElement.GetString();
                return QualityGateService.IsPathAllowed(policy, path);
            })
            .ToList();

        if (filteredSources.Count == mediaSources.GetArrayLength())
        {
            // No filtering needed
            return responseJson;
        }

        _logger.LogInformation(
            "QualityGate: Filtered MediaSources for user {UserId} - {Original} -> {Filtered} sources",
            userId, mediaSources.GetArrayLength(), filteredSources.Count);

        // Rebuild the JSON with filtered sources
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        
        writer.WriteStartObject();
        
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == "MediaSources")
            {
                writer.WritePropertyName("MediaSources");
                writer.WriteStartArray();
                foreach (var source in filteredSources)
                {
                    source.WriteTo(writer);
                }
                writer.WriteEndArray();
            }
            else
            {
                prop.WriteTo(writer);
            }
        }
        
        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}






