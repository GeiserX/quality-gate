using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.QualityGate.Library;

/// <summary>
/// Decides whether a path sits under a configured folder, and what is left of it below that folder.
/// </summary>
/// <remarks>
/// One rule, shared by version grouping and encode priority, so the two can never disagree about
/// which folder a file is in:
/// <list type="bullet">
/// <item>A path is under a root when it equals the root or starts with the root plus a separator,
/// so <c>/media/tv</c> never claims <c>/media/tv2/x</c>.</item>
/// <item>Trailing separators on either side are ignored.</item>
/// <item>Both separators are accepted on Windows only. On Linux and macOS <c>\</c> is an ordinary
/// filename character, not a separator.</item>
/// <item>Comparison is case-insensitive on Windows only.</item>
/// <item>A root made only of separators (<c>/</c>) matches nothing, as it always has for version
/// grouping. No real library is mounted at the filesystem root.</item>
/// </list>
/// </remarks>
internal static class PathRoots
{
    /// <summary>Checks whether <paramref name="path"/> is <paramref name="root"/> or inside it.</summary>
    /// <param name="path">The path to test.</param>
    /// <param name="root">The folder.</param>
    /// <returns>True when the path is the root or below it.</returns>
    public static bool IsUnder(string? path, string? root)
        => TryRelative(path, root, OperatingSystem.IsWindows(), out _);

    /// <summary>
    /// Gets the part of <paramref name="path"/> below <paramref name="root"/>, joined with <c>/</c>.
    /// </summary>
    /// <param name="path">The path to test.</param>
    /// <param name="root">The folder.</param>
    /// <param name="remainder">The rest of the path below the root, <c>/</c>-separated with no leading
    /// separator; empty when the path is the root itself.</param>
    /// <returns>True when the path is the root or below it.</returns>
    public static bool TryRelative(string? path, string? root, out string remainder)
        => TryRelative(path, root, OperatingSystem.IsWindows(), out remainder);

    /// <summary>
    /// Picks the longest root that contains <paramref name="path"/>, the most specific folder.
    /// </summary>
    /// <param name="path">The path to test.</param>
    /// <param name="roots">The candidate folders.</param>
    /// <param name="index">The index of the chosen root, or -1.</param>
    /// <param name="remainder">The rest of the path below the chosen root.</param>
    /// <returns>True when some root contains the path.</returns>
    public static bool TryLongestMatch(string? path, IReadOnlyList<string> roots, out int index, out string remainder)
        => TryLongestMatch(path, roots, OperatingSystem.IsWindows(), out index, out remainder);

    /// <summary>The rule with the platform made explicit, so both platforms can be tested anywhere.</summary>
    /// <param name="path">The path to test.</param>
    /// <param name="root">The folder.</param>
    /// <param name="isWindows">Whether to apply the Windows rules.</param>
    /// <param name="remainder">The rest of the path below the root.</param>
    /// <returns>True when the path is the root or below it.</returns>
    internal static bool TryRelative(string? path, string? root, bool isWindows, out string remainder)
    {
        remainder = string.Empty;
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
        {
            return false;
        }

        var subject = Normalize(path, isWindows);
        var folder = Normalize(root, isWindows);
        if (folder.Length == 0)
        {
            return false;
        }

        var separator = isWindows ? '\\' : '/';
        var comparison = isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (subject.Equals(folder, comparison))
        {
            return true;
        }

        if (subject.Length > folder.Length + 1
            && subject[folder.Length] == separator
            && subject.StartsWith(folder, comparison))
        {
            var rest = subject[(folder.Length + 1)..];
            remainder = isWindows ? rest.Replace('\\', '/') : rest;
            return true;
        }

        return false;
    }

    /// <summary>The longest-match rule with the platform made explicit.</summary>
    /// <param name="path">The path to test.</param>
    /// <param name="roots">The candidate folders.</param>
    /// <param name="isWindows">Whether to apply the Windows rules.</param>
    /// <param name="index">The index of the chosen root, or -1.</param>
    /// <param name="remainder">The rest of the path below the chosen root.</param>
    /// <returns>True when some root contains the path.</returns>
    internal static bool TryLongestMatch(string? path, IReadOnlyList<string> roots, bool isWindows, out int index, out string remainder)
    {
        index = -1;
        remainder = string.Empty;
        var bestLength = -1;

        for (var i = 0; i < roots.Count; i++)
        {
            if (!TryRelative(path, roots[i], isWindows, out var rest))
            {
                continue;
            }

            var length = Normalize(roots[i], isWindows).Length;
            if (length > bestLength)
            {
                bestLength = length;
                index = i;
                remainder = rest;
            }
        }

        return index >= 0;
    }

    private static string Normalize(string path, bool isWindows)
    {
        return isWindows
            ? path.Replace('/', '\\').TrimEnd('\\')
            : path.TrimEnd('/');
    }
}
