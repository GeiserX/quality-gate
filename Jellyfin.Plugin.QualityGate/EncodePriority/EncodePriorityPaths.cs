using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.QualityGate.Library;

namespace Jellyfin.Plugin.QualityGate.EncodePriority;

/// <summary>Where each target's file goes, and where the plugin keeps its own state.</summary>
/// <remarks>
/// Everything lives under <c>IApplicationPaths.DataPath</c>, never under the plugin's own data
/// folder. That one is <c>plugins/Jellyfin.Plugin.QualityGate_&lt;version&gt;</c> on a normal
/// install, so it moves on every upgrade and would break an encoder's bind mount.
/// </remarks>
internal static class EncodePriorityPaths
{
    /// <summary>Gets the folder that holds the state file and the Data folder lists.</summary>
    /// <param name="dataPath">Jellyfin's data folder.</param>
    /// <returns>The folder.</returns>
    public static string Root(string dataPath) => Path.Combine(dataPath, "quality-gate", "encode-priority");

    /// <summary>Gets the state file.</summary>
    /// <param name="dataPath">Jellyfin's data folder.</param>
    /// <returns>The file.</returns>
    public static string StateFile(string dataPath) => Path.Combine(Root(dataPath), "state.json");

    /// <summary>Gets a target's file in the Data folder mode: the first eight characters of its id.</summary>
    /// <param name="dataPath">Jellyfin's data folder.</param>
    /// <param name="targetId">The target's id.</param>
    /// <returns>The file.</returns>
    public static string DataFolderFile(string dataPath, string targetId)
    {
        var stem = new string(targetId.Take(8).Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
        return Path.Combine(Root(dataPath), stem + ".json");
    }

    /// <summary>Resolves the file a target writes, or says why it has none.</summary>
    /// <param name="target">The target.</param>
    /// <param name="dataPath">Jellyfin's data folder.</param>
    /// <param name="libraryLocations">Every library location, for the dotfile rule.</param>
    /// <param name="path">The file, as Jellyfin sees it.</param>
    /// <param name="error">Why there is none.</param>
    /// <returns>True when the target has a valid output file.</returns>
    public static bool TryResolveOutput(
        EncodeTargetOptions target,
        string dataPath,
        IReadOnlyCollection<string> libraryLocations,
        out string path,
        out string error)
    {
        path = string.Empty;
        error = string.Empty;

        switch (target.OutputMode)
        {
            case OutputMode.SourceFolder:
                var source = target.Folders.FirstOrDefault(f => f.EncoderPath.Length == 0);
                if (source is null)
                {
                    error = "the encoder's source folder is not a folder Jellyfin sees (no folder has an empty encoder path); use Data folder or Custom";
                    return false;
                }

                path = Path.Combine(source.JellyfinPath, PriorityFileWriter.SourceFolderFileName);
                return true;

            case OutputMode.DataFolder:
                path = DataFolderFile(dataPath, target.Id);
                return true;

            default:
                if (target.OutputPath.Length == 0 || !Path.IsPathRooted(target.OutputPath))
                {
                    error = "a custom output needs an absolute file path";
                    return false;
                }

                var name = Path.GetFileName(target.OutputPath);
                if (name.Length == 0)
                {
                    error = "a custom output must name a file, not a folder";
                    return false;
                }

                // A visible file inside a library would be picked up by the next scan.
                if (!name.StartsWith('.') && libraryLocations.Any(l => PathRoots.IsUnder(target.OutputPath, l)))
                {
                    error = $"{target.OutputPath} is inside a library, so its name must start with a dot";
                    return false;
                }

                path = target.OutputPath;
                return true;
        }
    }
}
