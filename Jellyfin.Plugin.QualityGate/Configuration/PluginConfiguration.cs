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

    /// <summary>
    /// Gets or sets the policy applied to requests that resolve to no user at all.
    /// Jellyfin's API-key authentication issues an empty user id, and so does an unauthenticated
    /// request, so neither carries a policy and both are served without a cap. That is right for a
    /// server-to-server integration and wrong when the key reaches something a capped user drives.
    /// Empty (the default) keeps them uncapped, so upgrading changes nothing until a policy is
    /// chosen here.
    /// </summary>
    public string ApiKeyPolicyId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether an encoded copy sitting beside its original is
    /// grouped with it as an alternate version.
    /// Jellyfin only does this inside a folder named after the film, so a library whose films sit
    /// loose at the root shows the copy as a separate film instead. Off by default: it changes how
    /// a library resolves, so it should be a deliberate choice.
    /// </summary>
    public bool EnableVersionGrouping { get; set; }

    /// <summary>
    /// Gets or sets the library paths where version grouping applies.
    /// Empty (the default) means every movie library.
    /// </summary>
    public List<string> VersionGroupingRoots { get; set; } = new();

    /// <summary>
    /// Gets or sets the filename suffixes that mark an encoded copy.
    /// A file pairs only when its name is exactly another file's name plus one of these, so
    /// "Movie - 720p.mp4" joins "Movie.mkv" while "Movie - German.mkv" does not.
    /// Empty (the default) means " - 720p", applied when the list is read. The list must start
    /// empty: XmlSerializer adds loaded items to whatever an initialiser put there.
    /// </summary>
    public List<string> VersionGroupingSuffixes { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether Quality Gate writes a list of what capped viewers
    /// are watching for an encoder to make first. Off by default. When off nothing is built, and
    /// any file the plugin wrote earlier is removed. Playback is never changed by this feature.
    /// </summary>
    public bool EnableEncodePriority { get; set; }

    /// <summary>Gets or sets one entry per encoder. Starts empty; see <see cref="EncodeTarget"/>.</summary>
    public List<EncodeTarget> EncodeTargets { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether items a capped viewer is playing right now count.</summary>
    public bool PriorityNowPlaying { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether resumable items per capped viewer count.</summary>
    public bool PriorityContinueWatching { get; set; } = true;

    /// <summary>Gets or sets the most resumable items considered per viewer (1 to 100).</summary>
    public int PriorityContinueWatchingPerUser { get; set; } = 20;

    /// <summary>Gets or sets a value indicating whether the next episodes of shows a capped viewer follows count.</summary>
    public bool PriorityNextUp { get; set; } = true;

    /// <summary>Gets or sets the episodes ahead per show, counting the next-up episode itself (1 to 20).</summary>
    public int PriorityNextUpDepth { get; set; } = 3;

    /// <summary>Gets or sets the most shows considered per viewer, most recently played first (1 to 50).</summary>
    public int PriorityNextUpShowsPerUser { get; set; } = 10;

    /// <summary>Gets or sets a value indicating whether season 0 is included in the lookahead.</summary>
    public bool PriorityNextUpIncludeSpecials { get; set; }

    /// <summary>Gets or sets a value indicating whether favourite movies and shows count.</summary>
    public bool PriorityFavourites { get; set; }

    /// <summary>Gets or sets the most favourites considered per viewer (1 to 200).</summary>
    public int PriorityFavouritesPerUser { get; set; } = 25;

    /// <summary>Gets or sets how many days of activity count (1 to 365).</summary>
    public int PriorityWatchedWithinDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets a value indicating whether a version with no known height counts as over the
    /// cap when the list is built. Playback is not affected.
    /// </summary>
    public bool PriorityUnprobedNeedsEncode { get; set; }

    /// <summary>Gets or sets a value indicating whether a run is queued shortly after a capped viewer starts playback.</summary>
    public bool PriorityRefreshOnPlayback { get; set; } = true;

    /// <summary>Gets or sets the minimum gap in minutes between playback-triggered runs (1 to 240).</summary>
    public int PriorityDebounceMinutes { get; set; } = 10;

    /// <summary>Gets or sets the hard runtime cap in seconds for one run (10 to 1800).</summary>
    public int PriorityRunBudgetSeconds { get; set; } = 180;
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
    /// Gets or sets the maximum video height, in pixels, that a user under this policy
    /// may be served (e.g. 720 for a 720p tier).
    /// The cap is evaluated against the media's actual height, read from its video
    /// <see cref="MediaBrowser.Model.Entities.MediaStream"/>, not against its filename.
    /// Zero (the default) disables height enforcement, so an existing configuration
    /// keeps its current behaviour until a height is chosen.
    /// </summary>
    public int MaxHeight { get; set; }

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

    /// <summary>
    /// Gets or sets the maximum video height for fallback transcoding (e.g. 720 for 720p).
    /// When greater than zero, forces the transcode output to be capped at this resolution.
    /// When zero (default), the transcode uses the source resolution (no cap).
    /// Only applies when <see cref="FallbackTranscode"/> is enabled.
    /// </summary>
    public int FallbackMaxHeight { get; set; }

    /// <summary>
    /// Gets or sets the maximum video bitrate in kbps for fallback transcoding.
    /// When greater than zero, overrides the automatic bitrate derived from resolution.
    /// When zero (default), the bitrate is calculated from <see cref="FallbackMaxHeight"/>.
    /// Only applies when <see cref="FallbackTranscode"/> is enabled.
    /// </summary>
    public int FallbackMaxBitrateKbps { get; set; }
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
