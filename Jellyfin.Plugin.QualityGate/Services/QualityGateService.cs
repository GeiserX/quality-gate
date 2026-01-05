using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.QualityGate.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QualityGate.Services;

/// <summary>
/// Service for applying quality gate policies to media access.
/// </summary>
public class QualityGateService
{
    private readonly ILogger<QualityGateService> _logger;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="QualityGateService"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{QualityGateService}"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    public QualityGateService(ILogger<QualityGateService> logger, IUserManager userManager)
    {
        _logger = logger;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets the policy assigned to a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>The assigned policy, or null if no policy is assigned.</returns>
    public QualityPolicy? GetUserPolicy(Guid userId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return null;
        }

        var userAssignment = config.UserPolicies.FirstOrDefault(up => up.UserId == userId);
        if (userAssignment == null || string.IsNullOrEmpty(userAssignment.PolicyId))
        {
            return null;
        }

        return config.Policies.FirstOrDefault(p => p.Id == userAssignment.PolicyId && p.Enabled);
    }

    /// <summary>
    /// Checks if a file path is allowed by the given policy.
    /// </summary>
    /// <param name="policy">The quality policy.</param>
    /// <param name="filePath">The file path to check.</param>
    /// <returns>True if allowed, false if blocked.</returns>
    public bool IsPathAllowed(QualityPolicy policy, string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return true; // Allow if no path (shouldn't happen)
        }

        // Check blocked paths first
        if (policy.BlockedPathPrefixes.Count > 0)
        {
            if (policy.BlockedPathPrefixes.Any(prefix => 
                filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogDebug("Path {Path} is blocked by policy {PolicyName}", filePath, policy.Name);
                return false;
            }
        }

        // If allowed paths are specified, file must match at least one
        if (policy.AllowedPathPrefixes.Count > 0)
        {
            var isAllowed = policy.AllowedPathPrefixes.Any(prefix => 
                filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            
            if (!isAllowed)
            {
                _logger.LogDebug("Path {Path} is not in allowed prefixes for policy {PolicyName}", filePath, policy.Name);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Filters media sources based on user policy.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="mediaSources">The available media sources.</param>
    /// <returns>Filtered list of media sources.</returns>
    public IEnumerable<MediaSourceInfo> FilterMediaSources(Guid userId, IEnumerable<MediaSourceInfo> mediaSources)
    {
        var policy = GetUserPolicy(userId);
        if (policy == null)
        {
            // No policy, return all sources
            return mediaSources;
        }

        _logger.LogInformation("Applying quality policy '{PolicyName}' to user {UserId}", policy.Name, userId);

        return mediaSources.Where(source => IsPathAllowed(policy, source.Path));
    }

    /// <summary>
    /// Checks if a user can access a specific media item based on its path.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="item">The media item.</param>
    /// <returns>True if access is allowed.</returns>
    public bool CanAccessItem(Guid userId, BaseItem item)
    {
        var policy = GetUserPolicy(userId);
        if (policy == null)
        {
            return true; // No policy, full access
        }

        var path = item.Path;
        if (string.IsNullOrEmpty(path))
        {
            return true; // No path to check
        }

        return IsPathAllowed(policy, path);
    }
}

