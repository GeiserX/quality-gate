using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.QualityGate.Configuration;

/// <summary>
/// One encoder process that reads one encode priority file.
/// </summary>
/// <remarks>
/// Every list starts empty. <c>XmlSerializer</c> adds loaded items to whatever an initialiser put
/// in a list, so a default inside one would gain a copy on every save and restart. Defaults are
/// applied when the configuration is read (<c>EncodePriorityOptions.From</c>), never stored here.
/// Enumerated fields are strings rather than C# enums: an unknown value then falls back to the
/// default when read, instead of failing the whole configuration save.
/// </remarks>
public class EncodeTarget
{
    /// <summary>Gets or sets the stable key for state, logs and the Data folder file name.</summary>
    /// <remarks>
    /// Empty by default; the settings page mints it. A random default would give a hand-written
    /// target with no <c>Id</c> element a new id on every load, dropping its state and renaming
    /// its Data folder file on each restart. A target with no id is ignored with a warning
    /// instead, until it is saved from the page.
    /// </remarks>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the label shown on the page, in the logs and in the file.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this encoder gets a list. A disabled one has its file removed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the folders Jellyfin sees, and where each sits inside the encoder's source folder.</summary>
    public List<EncodeFolderMapping> Folders { get; set; } = new();

    /// <summary>Gets or sets the height the encoder produces. jellyfin-encoder always produces 720.</summary>
    public int OutputHeight { get; set; } = 720;

    /// <summary>Gets or sets where the file goes: <c>SourceFolder</c>, <c>DataFolder</c> or <c>Custom</c>.</summary>
    public string OutputMode { get; set; } = "SourceFolder";

    /// <summary>Gets or sets the file, as Jellyfin sees it. Used only by the <c>Custom</c> mode.</summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>Gets or sets who counts: <c>Auto</c>, <c>Policies</c> or <c>Users</c>.</summary>
    public string AudienceMode { get; set; } = "Auto";

    /// <summary>Gets or sets the policies whose users count, in the <c>Policies</c> mode.</summary>
    public List<string> AudiencePolicyIds { get; set; } = new();

    /// <summary>Gets or sets the users who count, capped or not, in the <c>Users</c> mode.</summary>
    public List<Guid> AudienceUserIds { get; set; } = new();

    /// <summary>Gets or sets the users who never count.</summary>
    public List<Guid> ExcludedUserIds { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether each path is resolved to its final target before mapping.</summary>
    public bool ResolveSymlinks { get; set; }

    /// <summary>Gets or sets the longest list written.</summary>
    public int MaxEntries { get; set; } = 300;

    /// <summary>Gets or sets a value indicating whether the list is built and reported but no file is written.</summary>
    public bool DryRun { get; set; }
}

/// <summary>
/// "Jellyfin sees this folder as X; inside the encoder's source folder it is Y."
/// </summary>
public class EncodeFolderMapping
{
    /// <summary>Gets or sets the folder as Jellyfin sees it inside its container.</summary>
    public string JellyfinPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where that folder sits inside the encoder's source folder, relative and
    /// <c>/</c>-separated. Empty means it is the source folder itself.
    /// </summary>
    public string EncoderPath { get; set; } = string.Empty;
}
