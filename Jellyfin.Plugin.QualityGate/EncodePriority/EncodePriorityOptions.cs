using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.QualityGate.Configuration;

namespace Jellyfin.Plugin.QualityGate.EncodePriority;

/// <summary>Where a target's priority file goes.</summary>
internal enum OutputMode
{
    /// <summary>Beside the media, as <c>.encoder-priority.json</c> in the encoder's source folder.</summary>
    SourceFolder,

    /// <summary>Under Jellyfin's data folder, for an encoder that mounts it.</summary>
    DataFolder,

    /// <summary>A path the admin names.</summary>
    Custom,
}

/// <summary>Whose viewing drives a target's list.</summary>
internal enum AudienceMode
{
    /// <summary>Every capped user whose cap is at or above the encoder's output height.</summary>
    Auto,

    /// <summary>As <see cref="Auto"/>, limited to users on the chosen policies.</summary>
    Policies,

    /// <summary>The chosen users, capped or not.</summary>
    Users,
}

/// <summary>One folder mapping after validation: an absolute Jellyfin folder and a clean encoder path.</summary>
/// <param name="JellyfinPath">The folder as Jellyfin sees it, trailing separators trimmed.</param>
/// <param name="EncoderPath">Where it sits in the encoder's source folder, <c>/</c>-separated, no leading or trailing slash.</param>
internal sealed record FolderMapping(string JellyfinPath, string EncoderPath);

/// <summary>One encoder after validation, with every number in range and every string resolved.</summary>
internal sealed class EncodeTargetOptions
{
    /// <summary>Gets the stable key.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the label.</summary>
    public required string Name { get; init; }

    /// <summary>Gets a value indicating whether the admin switched this encoder on.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Gets the valid folder mappings.</summary>
    public required IReadOnlyList<FolderMapping> Folders { get; init; }

    /// <summary>Gets the height the encoder produces.</summary>
    public required int OutputHeight { get; init; }

    /// <summary>Gets where the file goes.</summary>
    public required OutputMode OutputMode { get; init; }

    /// <summary>Gets the file for the <see cref="OutputMode.Custom"/> mode.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Gets who counts.</summary>
    public required AudienceMode AudienceMode { get; init; }

    /// <summary>Gets the policies whose users count.</summary>
    public required IReadOnlySet<string> AudiencePolicyIds { get; init; }

    /// <summary>Gets the users who count.</summary>
    public required IReadOnlySet<Guid> AudienceUserIds { get; init; }

    /// <summary>Gets the users who never count.</summary>
    public required IReadOnlySet<Guid> ExcludedUserIds { get; init; }

    /// <summary>Gets a value indicating whether paths are resolved through symlinks before mapping.</summary>
    public required bool ResolveSymlinks { get; init; }

    /// <summary>Gets the longest list written.</summary>
    public required int MaxEntries { get; init; }

    /// <summary>Gets a value indicating whether the list is built but not written.</summary>
    public required bool DryRun { get; init; }

    /// <summary>Gets a value indicating whether this target takes part in a run: enabled, with at least one folder.</summary>
    public bool IsRunnable => Enabled && Folders.Count > 0;

    /// <summary>Gets the folders as a plain list of Jellyfin paths, in mapping order.</summary>
    public IReadOnlyList<string> JellyfinPaths => Folders.Select(f => f.JellyfinPath).ToArray();
}

/// <summary>
/// A snapshot of the encode priority settings with every default applied and every number in range.
/// </summary>
/// <remarks>
/// Validation on the settings page is client-side only, and a configuration can be edited by hand,
/// so nothing downstream trusts the raw values. Integers are clamped here rather than on save, an
/// unknown enumerated string falls back to its default, and a folder mapping that cannot be valid
/// is dropped. Each correction adds one line to <see cref="Warnings"/> for the log.
/// </remarks>
internal sealed class EncodePriorityOptions
{
    /// <summary>The most encoders one configuration may hold.</summary>
    public const int MaxTargets = 16;

    /// <summary>Gets a value indicating whether the feature is on.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Gets a value indicating whether items playing now count.</summary>
    public required bool NowPlaying { get; init; }

    /// <summary>Gets a value indicating whether resumable items count.</summary>
    public required bool ContinueWatching { get; init; }

    /// <summary>Gets the most resumable items per viewer.</summary>
    public required int ContinueWatchingPerUser { get; init; }

    /// <summary>Gets a value indicating whether next-up episodes count.</summary>
    public required bool NextUp { get; init; }

    /// <summary>Gets the episodes ahead per show, counting the next-up episode.</summary>
    public required int NextUpDepth { get; init; }

    /// <summary>Gets the most shows per viewer.</summary>
    public required int NextUpShowsPerUser { get; init; }

    /// <summary>Gets a value indicating whether season 0 is looked ahead into.</summary>
    public required bool NextUpIncludeSpecials { get; init; }

    /// <summary>Gets a value indicating whether favourites count.</summary>
    public required bool Favourites { get; init; }

    /// <summary>Gets the most favourites per viewer.</summary>
    public required int FavouritesPerUser { get; init; }

    /// <summary>Gets how many days of activity count.</summary>
    public required int WatchedWithinDays { get; init; }

    /// <summary>Gets a value indicating whether an unknown height counts as over the cap when building.</summary>
    public required bool UnprobedNeedsEncode { get; init; }

    /// <summary>Gets a value indicating whether playback start queues a run.</summary>
    public required bool RefreshOnPlayback { get; init; }

    /// <summary>Gets the minimum gap between playback-triggered runs.</summary>
    public required TimeSpan Debounce { get; init; }

    /// <summary>Gets the hard runtime cap for one run.</summary>
    public required TimeSpan RunBudget { get; init; }

    /// <summary>Gets every configured encoder, the first <see cref="MaxTargets"/> of them.</summary>
    public required IReadOnlyList<EncodeTargetOptions> Targets { get; init; }

    /// <summary>Gets one line per value that had to be corrected.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Gets the targets that take part in a run.</summary>
    public IEnumerable<EncodeTargetOptions> RunnableTargets => Targets.Where(t => t.IsRunnable);

    /// <summary>Builds the snapshot.</summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The options, with defaults applied and values in range.</returns>
    public static EncodePriorityOptions From(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var warnings = new List<string>();

        var targets = new List<EncodeTargetOptions>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configured = config.EncodeTargets ?? new List<EncodeTarget>();
        if (configured.Count > MaxTargets)
        {
            warnings.Add($"{configured.Count} encoders are configured; only the first {MaxTargets} are used.");
        }

        for (var i = 0; i < configured.Count && targets.Count < MaxTargets; i++)
        {
            var target = configured[i];
            if (target is null)
            {
                continue;
            }

            var id = (target.Id ?? string.Empty).Trim();
            if (id.Length == 0 || !seenIds.Add(id))
            {
                warnings.Add($"Encoder {i + 1} has a missing or repeated id and is ignored.");
                continue;
            }

            targets.Add(FromTarget(target, id, i, warnings));
        }

        return new EncodePriorityOptions
        {
            Enabled = config.EnableEncodePriority,
            NowPlaying = config.PriorityNowPlaying,
            ContinueWatching = config.PriorityContinueWatching,
            ContinueWatchingPerUser = Clamp(config.PriorityContinueWatchingPerUser, 1, 100, nameof(config.PriorityContinueWatchingPerUser), warnings),
            NextUp = config.PriorityNextUp,
            NextUpDepth = Clamp(config.PriorityNextUpDepth, 1, 20, nameof(config.PriorityNextUpDepth), warnings),
            NextUpShowsPerUser = Clamp(config.PriorityNextUpShowsPerUser, 1, 50, nameof(config.PriorityNextUpShowsPerUser), warnings),
            NextUpIncludeSpecials = config.PriorityNextUpIncludeSpecials,
            Favourites = config.PriorityFavourites,
            FavouritesPerUser = Clamp(config.PriorityFavouritesPerUser, 1, 200, nameof(config.PriorityFavouritesPerUser), warnings),
            WatchedWithinDays = Clamp(config.PriorityWatchedWithinDays, 1, 365, nameof(config.PriorityWatchedWithinDays), warnings),
            UnprobedNeedsEncode = config.PriorityUnprobedNeedsEncode,
            RefreshOnPlayback = config.PriorityRefreshOnPlayback,
            Debounce = TimeSpan.FromMinutes(Clamp(config.PriorityDebounceMinutes, 1, 240, nameof(config.PriorityDebounceMinutes), warnings)),
            RunBudget = TimeSpan.FromSeconds(Clamp(config.PriorityRunBudgetSeconds, 10, 1800, nameof(config.PriorityRunBudgetSeconds), warnings)),
            Targets = targets,
            Warnings = warnings,
        };
    }

    /// <summary>Cleans one folder's Jellyfin path: absolute, trailing separators trimmed.</summary>
    /// <param name="path">The configured path.</param>
    /// <returns>The clean path, or null when it cannot be valid.</returns>
    internal static string? CleanJellyfinPath(string? path)
    {
        var trimmed = (path ?? string.Empty).Trim();
        if (trimmed.Length == 0 || !Path.IsPathRooted(trimmed))
        {
            return null;
        }

        var clean = trimmed.TrimEnd('/', '\\');
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>
    /// Cleans one folder's encoder path: relative, <c>/</c>-separated, no <c>..</c>, no backslash.
    /// </summary>
    /// <param name="path">The configured path.</param>
    /// <returns>The clean path with no leading or trailing slash, or null when it cannot be valid.</returns>
    internal static string? CleanEncoderPath(string? path)
    {
        var trimmed = (path ?? string.Empty).Trim();
        if (trimmed.StartsWith('/') || trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p == ".."))
        {
            return null;
        }

        return string.Join('/', parts.Where(p => p != "."));
    }

    private static EncodeTargetOptions FromTarget(EncodeTarget target, string id, int index, List<string> warnings)
    {
        var name = (target.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            name = "Encoder " + (index + 1);
        }
        else if (name.Length > 64)
        {
            name = name[..64];
        }

        var folders = new List<FolderMapping>();
        foreach (var folder in target.Folders ?? new List<EncodeFolderMapping>())
        {
            if (folder is null)
            {
                continue;
            }

            var jellyfinPath = CleanJellyfinPath(folder.JellyfinPath);
            var encoderPath = CleanEncoderPath(folder.EncoderPath);
            if (jellyfinPath is null)
            {
                warnings.Add($"{name}: folder \"{folder.JellyfinPath}\" is not an absolute path and is ignored.");
                continue;
            }

            if (encoderPath is null)
            {
                warnings.Add($"{name}: encoder path \"{folder.EncoderPath}\" for {jellyfinPath} must be relative, use / and contain no .. ; the folder is ignored.");
                continue;
            }

            folders.Add(new FolderMapping(jellyfinPath, encoderPath));
        }

        if (target.Enabled && folders.Count == 0)
        {
            warnings.Add($"{name}: no valid folder, so no list is built for it.");
        }

        return new EncodeTargetOptions
        {
            Id = id,
            Name = name,
            Enabled = target.Enabled,
            Folders = folders,
            OutputHeight = Clamp(target.OutputHeight, 144, 4320, name + " output height", warnings),
            OutputMode = ParseEnum(target.OutputMode, OutputMode.SourceFolder, name + " output mode", warnings),
            OutputPath = (target.OutputPath ?? string.Empty).Trim(),
            AudienceMode = ParseEnum(target.AudienceMode, AudienceMode.Auto, name + " audience mode", warnings),
            AudiencePolicyIds = new HashSet<string>((target.AudiencePolicyIds ?? new List<string>()).Where(p => !string.IsNullOrEmpty(p)), StringComparer.Ordinal),
            AudienceUserIds = new HashSet<Guid>((target.AudienceUserIds ?? new List<Guid>()).Where(u => u != Guid.Empty)),
            ExcludedUserIds = new HashSet<Guid>(target.ExcludedUserIds ?? new List<Guid>()),
            ResolveSymlinks = target.ResolveSymlinks,
            MaxEntries = Clamp(target.MaxEntries, 1, 5000, name + " max entries", warnings),
            DryRun = target.DryRun,
        };
    }

    private static int Clamp(int value, int min, int max, string field, List<string> warnings)
    {
        if (value >= min && value <= max)
        {
            return value;
        }

        var clamped = Math.Clamp(value, min, max);
        warnings.Add($"{field} {value} is outside {min} to {max}; using {clamped}.");
        return clamped;
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback, string field, List<string> warnings)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        // Names only: Enum.TryParse would also accept "1" or "2", which no page ever writes.
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<TEnum>(name);
            }
        }

        warnings.Add($"{field} \"{value}\" is not known; using {fallback}.");
        return fallback;
    }
}
