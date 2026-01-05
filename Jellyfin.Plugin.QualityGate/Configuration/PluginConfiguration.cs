using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.QualityGate.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        Policies = new List<QualityPolicy>();
        UserPolicies = new List<UserPolicyAssignment>();
    }

    /// <summary>
    /// Gets or sets the list of quality policies.
    /// </summary>
    public List<QualityPolicy> Policies { get; set; }

    /// <summary>
    /// Gets or sets the user-to-policy assignments.
    /// </summary>
    public List<UserPolicyAssignment> UserPolicies { get; set; }
}

/// <summary>
/// Defines a quality restriction policy.
/// </summary>
public class QualityPolicy
{
    /// <summary>
    /// Gets or sets the unique policy identifier.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets or sets the policy name (e.g., "Low Bitrate Only", "Transcoded Files").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description of the policy.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of allowed path prefixes.
    /// Files must match at least one of these prefixes to be accessible.
    /// Example: ["/media-transcoded/"] to only allow transcoded files.
    /// </summary>
    public List<string> AllowedPathPrefixes { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of blocked path prefixes.
    /// Files matching these prefixes will be blocked.
    /// Example: ["/media/"] to block original high-quality files.
    /// </summary>
    public List<string> BlockedPathPrefixes { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether this policy is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Assigns a policy to a user.
/// </summary>
public class UserPolicyAssignment
{
    /// <summary>
    /// Gets or sets the Jellyfin user ID.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the username (for display purposes).
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the policy ID assigned to this user.
    /// Empty string means no policy (full access).
    /// </summary>
    public string PolicyId { get; set; } = string.Empty;
}

