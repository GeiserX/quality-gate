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
        DefaultPolicyId = string.Empty;
    }

    /// <summary>
    /// Gets or sets the list of quality policies.
    /// </summary>
    public List<QualityPolicy> Policies { get; set; }

    /// <summary>
    /// Gets or sets the user-to-policy assignments (overrides).
    /// </summary>
    public List<UserPolicyAssignment> UserPolicies { get; set; }

    /// <summary>
    /// Gets or sets the default policy ID applied to all users.
    /// Users with specific assignments in UserPolicies override this.
    /// Empty string means no default policy (full access for all).
    /// </summary>
    public string DefaultPolicyId { get; set; }

    /// <summary>
    /// Gets or sets the default intro video path for users without a policy-specific intro.
    /// Example: "/media/intros/GeiserLand.mp4"
    /// </summary>
    public string DefaultIntroVideoPath { get; set; } = string.Empty;
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
    /// Gets or sets regex patterns matched against filenames.
    /// Files whose filename matches at least one pattern are allowed.
    /// Example: ["- 720p", "- 1080p"] to only allow those versions.
    /// </summary>
    public List<string> AllowedFilenamePatterns { get; set; } = new();

    /// <summary>
    /// Gets or sets regex patterns matched against filenames.
    /// Files whose filename matches any pattern are blocked.
    /// Example: ["- 2160p", "- 4K"] to block UHD versions.
    /// </summary>
    public List<string> BlockedFilenamePatterns { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether this policy is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the header text shown when playback is blocked.
    /// </summary>
    public string BlockedMessageHeader { get; set; } = "Quality Restricted";

    /// <summary>
    /// Gets or sets the message shown when playback is blocked.
    /// </summary>
    public string BlockedMessageText { get; set; } = "This quality version is not available for your account.";

    /// <summary>
    /// Gets or sets the timeout in milliseconds for the blocked message.
    /// </summary>
    public long BlockedMessageTimeoutMs { get; set; } = 8000;

    /// <summary>
    /// Gets or sets the path to a custom intro video for users under this policy.
    /// If empty, uses Jellyfin's default intro video (if configured in plugin intros).
    /// Example: "/media/intros/720p-intro.mp4"
    /// </summary>
    public string IntroVideoPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to fall back to server-side transcoding
    /// when no media source matches the allowed filename patterns.
    /// When enabled, instead of blocking playback, the original file is served
    /// with direct play/stream disabled, forcing Jellyfin to transcode.
    /// </summary>
    public bool FallbackTranscode { get; set; }
}

/// <summary>
/// Assigns a policy to a user (overrides the default policy).
/// </summary>
public class UserPolicyAssignment
{
    /// <summary>
    /// Special policy ID that indicates full access (no restrictions).
    /// </summary>
    public const string FullAccessPolicyId = "__FULL_ACCESS__";

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
    /// Use "__FULL_ACCESS__" to give unrestricted access.
    /// Empty string means use the default policy.
    /// </summary>
    public string PolicyId { get; set; } = string.Empty;
}
