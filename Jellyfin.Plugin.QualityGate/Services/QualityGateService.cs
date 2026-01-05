using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.QualityGate.Configuration;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.QualityGate.Services;

/// <summary>
/// Service for applying quality gate policies to media access.
/// </summary>
public static class QualityGateService
{
    /// <summary>
    /// Gets the effective policy for a user.
    /// Priority: User-specific override > Default policy > No policy (full access).
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>The effective policy, or null if user has full access.</returns>
    public static QualityPolicy? GetUserPolicy(Guid userId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return null;
        }

        // Check for user-specific override
        var userAssignment = config.UserPolicies.FirstOrDefault(up => up.UserId == userId);
        
        if (userAssignment != null)
        {
            // User has a specific assignment
            if (userAssignment.PolicyId == UserPolicyAssignment.FullAccessPolicyId)
            {
                // User explicitly has full access
                return null;
            }

            if (!string.IsNullOrEmpty(userAssignment.PolicyId))
            {
                // User has a specific policy
                var policy = config.Policies.FirstOrDefault(p => p.Id == userAssignment.PolicyId && p.Enabled);
                if (policy != null)
                {
                    return policy;
                }
            }
        }

        // No user-specific override, check for default policy
        if (!string.IsNullOrEmpty(config.DefaultPolicyId))
        {
            var defaultPolicy = config.Policies.FirstOrDefault(p => p.Id == config.DefaultPolicyId && p.Enabled);
            if (defaultPolicy != null)
            {
                return defaultPolicy;
            }
        }

        // No policy applies - full access
        return null;
    }

    /// <summary>
    /// Checks if a file path is allowed by the given policy.
    /// </summary>
    /// <param name="policy">The quality policy.</param>
    /// <param name="filePath">The file path to check.</param>
    /// <returns>True if allowed, false if blocked.</returns>
    public static bool IsPathAllowed(QualityPolicy policy, string? filePath)
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
    public static IEnumerable<MediaSourceInfo> FilterMediaSources(Guid userId, IEnumerable<MediaSourceInfo> mediaSources)
    {
        var policy = GetUserPolicy(userId);
        if (policy == null)
        {
            // No policy, return all sources
            return mediaSources;
        }

        return mediaSources.Where(source => IsPathAllowed(policy, source.Path));
    }

    /// <summary>
    /// Checks if a user can access a specific file path.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="path">The file path.</param>
    /// <returns>True if access is allowed.</returns>
    public static bool CanAccessPath(Guid userId, string? path)
    {
        var policy = GetUserPolicy(userId);
        if (policy == null)
        {
            return true; // No policy, full access
        }

        return IsPathAllowed(policy, path);
    }

    /// <summary>
    /// Checks if a user has full access (no restrictions).
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>True if user has full access.</returns>
    public static bool HasFullAccess(Guid userId)
    {
        return GetUserPolicy(userId) == null;
    }
}
