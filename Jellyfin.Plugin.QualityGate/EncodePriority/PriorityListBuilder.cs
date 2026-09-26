using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.QualityGate.Library;
using Jellyfin.Plugin.QualityGate.Services;

namespace Jellyfin.Plugin.QualityGate.EncodePriority;

/// <summary>A user as the list builder needs to see them.</summary>
/// <param name="Id">The user id.</param>
/// <param name="Disabled">Whether the account is disabled.</param>
/// <param name="LastActivity">The user's last activity.</param>
/// <param name="Cap">The user's height cap; 0 when uncapped.</param>
/// <param name="PolicyId">The id of the policy the user resolves to, or null.</param>
/// <param name="DenyAll">Whether the user resolves to the deny-all sentinel (a deleted or disabled policy).</param>
internal sealed record Viewer(Guid Id, bool Disabled, DateTime? LastActivity, int Cap, string? PolicyId, bool DenyAll);

/// <summary>A library and the folders it reads.</summary>
/// <param name="Name">The library's name.</param>
/// <param name="Locations">Its folders, as Jellyfin sees them.</param>
internal sealed record LibraryFolder(string Name, IReadOnlyList<string> Locations);

/// <summary>A policy, for the "no viewer benefits" finding.</summary>
/// <param name="Name">The policy's name.</param>
/// <param name="MaxHeight">Its cap.</param>
internal sealed record PolicySummary(string Name, int MaxHeight);

/// <summary>One thing the admin should know about a target, with the fix.</summary>
/// <param name="Code">A stable code, for the page and the logs.</param>
/// <param name="Message">One line, with the fix.</param>
/// <param name="Examples">Up to five example paths, when they help.</param>
internal sealed record Finding(string Code, string Message, IReadOnlyList<string>? Examples = null);

/// <summary>One line of a priority file, with why it is there.</summary>
internal sealed class ListedEntry
{
    /// <summary>Gets or sets the path as the encoder sees it.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the item a viewer asked for.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the version whose file this is.</summary>
    public Guid VersionId { get; set; }

    /// <summary>Gets or sets the height of that version, or null when unknown.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets the best tier among the viewers asking for it.</summary>
    public int Tier { get; set; }

    /// <summary>Gets or sets the best lookahead depth.</summary>
    public int Depth { get; set; }

    /// <summary>Gets or sets how many viewers ask for it.</summary>
    public int Users { get; set; }

    /// <summary>Gets or sets the most recent interest.</summary>
    public DateTime LastActivity { get; set; }

    /// <summary>Gets or sets the tallest cap for which the item is a gap. A version at or below it covers the item.</summary>
    public int GapCap { get; set; }

    /// <summary>Gets or sets the reasons, for the preview.</summary>
    public List<string> Reasons { get; set; } = new();
}

/// <summary>The counts shown for one target.</summary>
internal sealed class TargetCounts
{
    /// <summary>Gets or sets the viewers in the audience.</summary>
    public int Viewers { get; set; }

    /// <summary>Gets or sets the distinct items the audience asks for.</summary>
    public int DemandItems { get; set; }

    /// <summary>Gets or sets the items a viewer would get as a live transcode.</summary>
    public int Gaps { get; set; }

    /// <summary>Gets or sets the entries written.</summary>
    public int Listed { get; set; }

    /// <summary>Gets or sets the listed items that gained a within-cap version since the last run.</summary>
    public int Covered { get; set; }

    /// <summary>Gets or sets the asked-for items with no known height on any version.</summary>
    public int HeightUnknown { get; set; }

    /// <summary>Gets or sets the gaps under none of this encoder's folders.</summary>
    public int Unmapped { get; set; }

    /// <summary>Gets or sets the entries dropped by the list limit.</summary>
    public int CutByLimit { get; set; }

    /// <summary>Gets or sets the symlinks that could not be resolved.</summary>
    public int SymlinkFailures { get; set; }
}

/// <summary>The list for one target, before it is written.</summary>
internal sealed class TargetBuild
{
    /// <summary>Gets or sets the target's id.</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>Gets or sets the target's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the entries, in file order.</summary>
    public List<ListedEntry> Entries { get; set; } = new();

    /// <summary>Gets or sets the counts.</summary>
    public TargetCounts Counts { get; set; } = new();

    /// <summary>Gets or sets the findings.</summary>
    public List<Finding> Findings { get; set; } = new();

    /// <summary>Gets or sets the audience: each viewer and the cap used for them.</summary>
    public IReadOnlyDictionary<Guid, int> Audience { get; set; } = new Dictionary<Guid, int>();

    /// <summary>Gets the paths, in file order.</summary>
    public IReadOnlyList<string> Paths => Entries.Select(e => e.Path).ToList();
}

/// <summary>Everything a build reads, gathered by the task so the builder itself does no IO.</summary>
internal sealed class BuildInput
{
    /// <summary>Gets or sets the settings.</summary>
    public required EncodePriorityOptions Options { get; init; }

    /// <summary>Gets or sets every user.</summary>
    public required IReadOnlyList<Viewer> Viewers { get; init; }

    /// <summary>Gets or sets what the viewers asked for.</summary>
    public required CollectedDemand Demand { get; init; }

    /// <summary>Gets or sets the libraries.</summary>
    public required IReadOnlyList<LibraryFolder> Libraries { get; init; }

    /// <summary>Gets or sets the enabled policies.</summary>
    public required IReadOnlyList<PolicySummary> Policies { get; init; }

    /// <summary>Gets or sets the current time.</summary>
    public required DateTime NowUtc { get; init; }

    /// <summary>Gets or sets a check that a folder exists on the Jellyfin host.</summary>
    public Func<string, bool> DirectoryExists { get; init; } = _ => true;

    /// <summary>Gets or sets a symlink resolver: the final target, or null when it cannot be resolved.</summary>
    public Func<string, string?> ResolveLink { get; init; } = p => p;
}

/// <summary>
/// Turns demand into one ordered list per target. Pure: the same input always gives the same output.
/// </summary>
internal static class PriorityListBuilder
{
    /// <summary>The share of gaps outside every folder above which the target is flagged.</summary>
    public const double MostlyUnmappedShare = 0.25;

    private const int MaxExamples = 5;

    /// <summary>Gets the users any runnable target counts, so the collector queries each once.</summary>
    /// <param name="options">The settings.</param>
    /// <param name="viewers">Every user.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns>The user ids.</returns>
    public static IReadOnlySet<Guid> AudienceUnion(EncodePriorityOptions options, IReadOnlyList<Viewer> viewers, DateTime nowUtc)
    {
        var union = new HashSet<Guid>();
        foreach (var target in options.RunnableTargets)
        {
            union.UnionWith(ResolveAudience(target, viewers, options, nowUtc, out _).Keys);
        }

        return union;
    }

    /// <summary>
    /// Decides who counts for a target, and at which cap.
    /// </summary>
    /// <remarks>
    /// An encode at height H helps a viewer capped at C only when H is at most C, so in the
    /// automatic mode a 720p encoder serves 720p and 1080p viewers and cannot help 480p ones.
    /// A user on a deleted or disabled policy resolves to the deny-all sentinel; playback treats
    /// them as uncapped, so they are never audience here, in any mode.
    /// </remarks>
    /// <param name="target">The target.</param>
    /// <param name="viewers">Every user.</param>
    /// <param name="options">The settings.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <param name="belowOutput">Users mode only: chosen users capped below the output height.</param>
    /// <returns>Each counted user and the cap to test gaps at.</returns>
    public static Dictionary<Guid, int> ResolveAudience(EncodeTargetOptions target, IReadOnlyList<Viewer> viewers, EncodePriorityOptions options, DateTime nowUtc, out int belowOutput)
    {
        belowOutput = 0;
        var cutoff = nowUtc.AddDays(-options.WatchedWithinDays);
        var audience = new Dictionary<Guid, int>();

        foreach (var viewer in viewers)
        {
            if (viewer.Disabled
                || viewer.DenyAll
                || target.ExcludedUserIds.Contains(viewer.Id)
                || CandidateCollector.IsIdle(viewer.LastActivity, cutoff))
            {
                continue;
            }

            switch (target.AudienceMode)
            {
                case AudienceMode.Users:
                    if (!target.AudienceUserIds.Contains(viewer.Id))
                    {
                        break;
                    }

                    if (viewer.Cap > 0 && viewer.Cap < target.OutputHeight)
                    {
                        belowOutput++;
                        break;
                    }

                    // An uncapped user the admin chose is treated as capped at what the encoder makes.
                    audience[viewer.Id] = viewer.Cap > 0 ? viewer.Cap : target.OutputHeight;
                    break;

                case AudienceMode.Policies:
                    if (viewer.Cap >= target.OutputHeight && viewer.PolicyId is not null && target.AudiencePolicyIds.Contains(viewer.PolicyId))
                    {
                        audience[viewer.Id] = viewer.Cap;
                    }

                    break;

                default:
                    if (viewer.Cap > 0 && viewer.Cap >= target.OutputHeight)
                    {
                        audience[viewer.Id] = viewer.Cap;
                    }

                    break;
            }
        }

        return audience;
    }

    /// <summary>Builds one target's list.</summary>
    /// <param name="target">The target.</param>
    /// <param name="input">Everything the build reads.</param>
    /// <returns>The list, its counts and its findings.</returns>
    public static TargetBuild Build(EncodeTargetOptions target, BuildInput input)
    {
        var options = input.Options;
        var build = new TargetBuild { TargetId = target.Id, Name = target.Name };
        var audience = ResolveAudience(target, input.Viewers, options, input.NowUtc, out var belowOutput);
        build.Audience = audience;
        build.Counts.Viewers = audience.Count;

        AddFolderFindings(target, input, build);

        if (audience.Count == 0)
        {
            var policies = input.Policies.Where(p => p.MaxHeight > 0).Select(p => $"{p.Name} ({p.MaxHeight}p)").ToList();
            build.Findings.Add(new Finding(
                "NoAudience",
                $"No viewer benefits from this encoder's {target.OutputHeight}p output. "
                + (policies.Count == 0 ? "No policy has a resolution cap." : "Policies and their caps: " + string.Join(", ", policies) + ".")));
        }

        if (belowOutput > 0)
        {
            build.Findings.Add(new Finding(
                "AudienceBelowOutput",
                $"{belowOutput} chosen viewers are capped below this encoder's {target.OutputHeight}p output, so its copies cannot help them. Those viewers keep getting live transcodes."));
        }

        // What every counted viewer asked for, per item.
        var demanded = new Dictionary<Guid, List<DemandSignal>>();
        foreach (var signal in input.Demand.Signals)
        {
            if (!audience.ContainsKey(signal.UserId))
            {
                continue;
            }

            if (!demanded.TryGetValue(signal.ItemId, out var signals))
            {
                signals = new List<DemandSignal>();
                demanded[signal.ItemId] = signals;
            }

            signals.Add(signal);
        }

        build.Counts.DemandItems = demanded.Count;

        // The encoders that write a list, with the viewers each counts, for telling this
        // encoder's unmapped gaps from gaps another encoder lists.
        var others = options.RunnableTargets
            .Where(other => other.Id != target.Id && !other.DryRun)
            .Select(other => (Target: other, Audience: ResolveAudience(other, input.Viewers, options, input.NowUtc, out _)))
            .ToList();
        var everyAsk = input.Demand.Signals.ToLookup(s => s.ItemId);

        var listed = new List<(Candidate Candidate, List<ListedEntry> Entries)>();
        var unmapped = new List<string>();
        foreach (var (itemId, signals) in demanded)
        {
            var versions = input.Demand.Versions.GetValueOrDefault(itemId) ?? Array.Empty<VersionInfo>();
            var heights = versions.Select(v => v.Height).ToArray();
            if (heights.Length > 0 && heights.All(h => !h.HasValue))
            {
                build.Counts.HeightUnknown++;
            }

            // One candidate per item, holding the best tier and depth of the viewers for whom the
            // item is a gap, tested at each viewer's own cap. A viewer who already has a version
            // within their cap does not need the encode, so their demand does not rank it or
            // give it a reason.
            var candidate = new Candidate(itemId);
            var gapCap = 0;
            var lowestGapCap = int.MaxValue;
            foreach (var signal in signals)
            {
                var cap = audience[signal.UserId];
                if (QualityGateService.IsCapGap(heights, cap, options.UnprobedNeedsEncode))
                {
                    candidate.Merge(signal);
                    gapCap = Math.Max(gapCap, cap);
                    lowestGapCap = Math.Min(lowestGapCap, cap);
                }
            }

            if (gapCap == 0 || target.OutputHeight > gapCap)
            {
                continue;
            }

            // Every version the encoder could start from, the cheapest (lowest) first. Listing the
            // others is harmless: the encoder skips a file whose output already exists.
            var sources = versions
                .Where(v => v.Height.HasValue ? v.Height.Value > target.OutputHeight : options.UnprobedNeedsEncode)
                .OrderBy(v => v.Height ?? int.MaxValue)
                .ThenBy(v => v.Path, StringComparer.Ordinal)
                .ToList();

            var entries = new List<ListedEntry>();
            var missed = new List<string>();
            foreach (var source in sources)
            {
                var path = source.Path;
                if (target.ResolveSymlinks)
                {
                    var resolved = input.ResolveLink(path);
                    if (resolved is null)
                    {
                        build.Counts.SymlinkFailures++;
                    }
                    else
                    {
                        path = resolved;
                    }
                }

                if (MapToEncoderPath(path, target.Folders) is not { } encoderPath)
                {
                    missed.Add(path);
                    continue;
                }

                entries.Add(new ListedEntry
                {
                    Path = encoderPath,
                    ItemId = candidate.ItemId,
                    VersionId = source.Id,
                    Height = source.Height,
                    Tier = candidate.Tier,
                    Depth = candidate.Depth,
                    Users = candidate.UserIds.Count,
                    LastActivity = candidate.LastActivity,
                    GapCap = gapCap,
                    Reasons = candidate.Reasons(),
                });
            }

            if (entries.Count == 0)
            {
                // A gap another encoder makes the copy for is that encoder's gap. With one encoder
                // per library, counting it here would flag every encoder for the others' libraries.
                if (!others.Any(other => ListsGap(other.Target, other.Audience, everyAsk[itemId], heights, versions, lowestGapCap, input)))
                {
                    build.Counts.Gaps++;
                    build.Counts.Unmapped++;
                    unmapped.AddRange(missed);
                }

                continue;
            }

            build.Counts.Gaps++;
            listed.Add((candidate, entries));
        }

        // Depth before breadth: every show a viewer follows gets its next episode before any show
        // gets its third. The path last makes the order, and so the file, deterministic.
        var ordered = listed
            .OrderBy(x => x.Candidate.Tier)
            .ThenBy(x => x.Candidate.Depth)
            .ThenByDescending(x => x.Candidate.UserIds.Count)
            .ThenByDescending(x => x.Candidate.LastActivity)
            .ThenBy(x => x.Entries[0].Path, StringComparer.Ordinal)
            .SelectMany(x => x.Entries);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in ordered)
        {
            if (!seen.Add(entry.Path))
            {
                continue;
            }

            if (build.Entries.Count >= target.MaxEntries)
            {
                build.Counts.CutByLimit++;
                continue;
            }

            build.Entries.Add(entry);
        }

        build.Counts.Listed = build.Entries.Count;

        if (build.Counts.Gaps > 0 && build.Counts.Unmapped > build.Counts.Gaps * MostlyUnmappedShare)
        {
            build.Findings.Add(MostlyUnmapped(build.Counts, unmapped, input.Libraries));
        }

        if (build.Counts.HeightUnknown > 0)
        {
            build.Findings.Add(new Finding(
                "UnknownHeights",
                $"{build.Counts.HeightUnknown} watched items have never been probed. Run Extract media info or a library scan so Quality Gate can see their resolution."));
        }

        return build;
    }

    /// <summary>
    /// Maps a path Jellyfin sees onto the encoder's source folder, through the most specific folder.
    /// </summary>
    /// <remarks>
    /// The result is byte-identical to the input below the folder: no case folding and no Unicode
    /// normalisation, because the encoder compares exact path components.
    /// </remarks>
    /// <param name="path">The file as Jellyfin sees it.</param>
    /// <param name="folders">The target's folders.</param>
    /// <returns>The path as the encoder sees it, or null when no folder contains it.</returns>
    public static string? MapToEncoderPath(string path, IReadOnlyList<FolderMapping> folders)
    {
        if (!PathRoots.TryLongestMatch(path, folders.Select(f => f.JellyfinPath).ToList(), out var index, out var remainder)
            || remainder.Length == 0)
        {
            return null;
        }

        var prefix = folders[index].EncoderPath;
        return prefix.Length == 0 ? remainder : prefix + "/" + remainder;
    }

    /// <summary>
    /// Whether an encoder would list this item, by the same tests <see cref="Build"/> applies,
    /// with a copy every one of this encoder's gap viewers can use: a viewer it counts asked for
    /// the item, the item is a gap at that viewer's cap, its copy fits the largest such cap and
    /// the lowest cap here, and one of its folders holds a source to encode.
    /// </summary>
    private static bool ListsGap(EncodeTargetOptions other, Dictionary<Guid, int> audience, IEnumerable<DemandSignal> signals, int?[] heights, IReadOnlyList<VersionInfo> versions, int lowestCapHere, BuildInput input)
    {
        // A taller copy leaves the lower-capped viewers here on a live transcode.
        if (other.OutputHeight > lowestCapHere)
        {
            return false;
        }

        var gapCap = 0;
        foreach (var signal in signals)
        {
            if (audience.TryGetValue(signal.UserId, out var cap) && QualityGateService.IsCapGap(heights, cap, input.Options.UnprobedNeedsEncode))
            {
                gapCap = Math.Max(gapCap, cap);
            }
        }

        if (gapCap == 0 || other.OutputHeight > gapCap)
        {
            return false;
        }

        return versions
            .Where(v => v.Height.HasValue ? v.Height.Value > other.OutputHeight : input.Options.UnprobedNeedsEncode)
            .Any(source =>
            {
                var path = other.ResolveSymlinks ? input.ResolveLink(source.Path) ?? source.Path : source.Path;
                return MapToEncoderPath(path, other.Folders) is not null;
            });
    }

    private static void AddFolderFindings(EncodeTargetOptions target, BuildInput input, TargetBuild build)
    {
        var locations = input.Libraries.SelectMany(l => l.Locations).ToList();
        foreach (var folder in target.Folders)
        {
            if (!input.DirectoryExists(folder.JellyfinPath))
            {
                build.Findings.Add(new Finding(
                    "FolderMissing",
                    $"{folder.JellyfinPath} does not exist on the Jellyfin host. Use the path Jellyfin sees inside its container, not the host path."));
                continue;
            }

            if (locations.Count > 0
                && !locations.Any(l => PathRoots.IsUnder(folder.JellyfinPath, l) || PathRoots.IsUnder(l, folder.JellyfinPath)))
            {
                build.Findings.Add(new Finding(
                    "FolderNotInLibrary",
                    $"{folder.JellyfinPath} is under no library location, so no library item can ever be under it. Library locations: {string.Join(", ", locations)}."));
            }
        }
    }

    private static Finding MostlyUnmapped(TargetCounts counts, List<string> unmapped, IReadOnlyList<LibraryFolder> libraries)
    {
        // Name the folder most of the unmapped gaps sit in, so the fix is obvious.
        var byFolder = unmapped
            .Select(path =>
            {
                string? bestLocation = null;
                string? bestLibrary = null;
                foreach (var library in libraries)
                {
                    foreach (var location in library.Locations)
                    {
                        if (PathRoots.IsUnder(path, location) && (bestLocation is null || location.Length > bestLocation.Length))
                        {
                            bestLocation = location;
                            bestLibrary = library.Name;
                        }
                    }
                }

                return (Location: bestLocation, Library: bestLibrary, Path: path);
            })
            .GroupBy(x => (x.Location, x.Library))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.Location, StringComparer.Ordinal)
            .First();

        var where = byFolder.Key.Location is null
            ? "under no library location"
            : $"under {byFolder.Key.Location} (library {byFolder.Key.Library})";
        var examples = byFolder.Select(x => x.Path).Distinct(StringComparer.Ordinal).Take(MaxExamples).ToList();

        return new Finding(
            "MostlyUnmapped",
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} of {1} watched gaps are {2}, which this encoder does not cover. If that is a tree of links, add the tree as a folder and turn on Resolve symlinks, or point the folder at the real files.",
                counts.Unmapped,
                counts.Gaps,
                where),
            examples);
    }

    /// <summary>One demanded item and the best evidence for it.</summary>
    private sealed class Candidate
    {
        private readonly HashSet<(int Tier, int Depth)> _reasons = new();

        public Candidate(Guid itemId)
        {
            ItemId = itemId;
        }

        public Guid ItemId { get; }

        public int Tier { get; private set; } = int.MaxValue;

        public int Depth { get; private set; } = int.MaxValue;

        public DateTime LastActivity { get; private set; } = DateTime.MinValue;

        public HashSet<Guid> UserIds { get; } = new();

        public void Merge(DemandSignal signal)
        {
            if ((signal.Tier, signal.Depth).CompareTo((Tier, Depth)) < 0)
            {
                Tier = signal.Tier;
                Depth = signal.Depth;
            }

            if (signal.LastActivity > LastActivity)
            {
                LastActivity = signal.LastActivity;
            }

            UserIds.Add(signal.UserId);
            _reasons.Add((signal.Tier, signal.Depth));
        }

        public List<string> Reasons() => _reasons
            .OrderBy(r => r.Tier)
            .ThenBy(r => r.Depth)
            .Select(r => r.Depth > 1 ? $"{DemandTier.Reason(r.Tier)} +{r.Depth - 1}" : DemandTier.Reason(r.Tier))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
