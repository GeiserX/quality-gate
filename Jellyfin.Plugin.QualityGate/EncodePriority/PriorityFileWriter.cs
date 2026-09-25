using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.QualityGate.EncodePriority;

/// <summary>What happened when a list was written.</summary>
internal enum WriteOutcome
{
    /// <summary>The file was created or its paths changed, and it was replaced.</summary>
    Written,

    /// <summary>The paths were the same, so the file was left alone and its mtime did not move.</summary>
    Unchanged,

    /// <summary>The paths were the same but the file was over a day old, so it was rewritten with a fresh timestamp.</summary>
    Heartbeat,

    /// <summary>A file the plugin did not write is at the path, so nothing was written.</summary>
    Foreign,

    /// <summary>The write failed. The previous file, if any, is untouched.</summary>
    Failed,
}

/// <summary>What happened when the cleanup pass tried to remove a file the plugin wrote earlier.</summary>
internal enum RemoveOutcome
{
    /// <summary>The file was deleted.</summary>
    Deleted,

    /// <summary>The file was already gone.</summary>
    Missing,

    /// <summary>Something else now sits at the path; it was left alone.</summary>
    NotOurs,

    /// <summary>The delete failed, so the file now holds an empty list.</summary>
    Emptied,

    /// <summary>Both the delete and the empty write failed; the record is kept for a retry.</summary>
    Failed,
}

/// <summary>The result of one write.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Error">The reason, for <see cref="WriteOutcome.Foreign"/> and <see cref="WriteOutcome.Failed"/>.</param>
internal sealed record WriteResult(WriteOutcome Outcome, string? Error = null)
{
    /// <summary>Gets a value indicating whether the file on disk now holds the list.</summary>
    public bool Succeeded => Outcome is WriteOutcome.Written or WriteOutcome.Unchanged or WriteOutcome.Heartbeat;
}

/// <summary>The priority file as the encoder reads it. The encoder reads only <see cref="Paths"/>.</summary>
internal sealed class PriorityFile
{
    /// <summary>Gets or sets when the list was built, UTC, to the second.</summary>
    [JsonPropertyName("generated")]
    public string? Generated { get; set; }

    /// <summary>Gets or sets the marker the plugin checks before it overwrites or deletes a file.</summary>
    [JsonPropertyName("producer")]
    public string? Producer { get; set; }

    /// <summary>Gets or sets the encoder's name, for a human reading the file.</summary>
    [JsonPropertyName("target")]
    public string? Target { get; set; }

    /// <summary>Gets or sets the files to encode first, relative to the encoder's source folder.</summary>
    [JsonPropertyName("paths")]
    public List<string>? Paths { get; set; }
}

/// <summary>
/// Writes and removes priority files. Every rule here protects either the encoder or a file the
/// plugin did not write.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A file that does not parse, or parses without <c>"producer":"quality-gate"</c>, is never
/// overwritten or deleted. It may be a list someone wrote by hand.</item>
/// <item>The file is replaced only when its paths change. Every mtime change makes the encoder
/// rebuild its queue and log a line. A file older than a day is rewritten with a fresh
/// <c>generated</c> so an age check in the encoder can be tight.</item>
/// <item>Writes are atomic: <c>.name.tmp</c> in the same folder, created exclusively so a
/// symlink planted there is never followed, flushed, then moved over the target. Both names
/// start with a dot, which the encoder and Jellyfin's library monitor ignore. Any failure
/// deletes the temp file and leaves the previous file in force.</item>
/// <item>UTF-8 without a byte order mark: the encoder's <c>json.load</c> rejects one.</item>
/// </list>
/// </remarks>
internal static class PriorityFileWriter
{
    /// <summary>The marker every file the plugin writes carries.</summary>
    public const string Producer = "quality-gate";

    /// <summary>jellyfin-encoder's default <c>PRIORITY_FILE</c> name inside its source folder.</summary>
    public const string SourceFolderFileName = ".encoder-priority.json";

    /// <summary>How old an unchanged file may get before it is rewritten.</summary>
    public static readonly TimeSpan HeartbeatAge = TimeSpan.FromHours(24);

    /// <summary>
    /// The largest file read back: far above the biggest list the plugin writes (5,000 paths),
    /// so only something else is ever refused for its size.
    /// </summary>
    private const long MaxFileBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Writes a list, following every rule in the remarks.</summary>
    /// <param name="path">The file, as Jellyfin sees it.</param>
    /// <param name="targetName">The encoder's name.</param>
    /// <param name="paths">The entries, already ordered and cut.</param>
    /// <param name="createDirectory">Whether a missing folder may be created (the Data folder only).</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns>What happened.</returns>
    public static WriteResult Write(string path, string targetName, IReadOnlyList<string> paths, bool createDirectory, DateTime nowUtc)
    {
        try
        {
            var existing = Read(path);
            if (existing.Exists && !existing.IsOurs)
            {
                return new WriteResult(
                    WriteOutcome.Foreign,
                    $"a file the plugin did not write is at {path}; delete it or choose another output");
            }

            if (existing.IsOurs && existing.Paths.SequenceEqual(paths, StringComparer.Ordinal))
            {
                if (existing.Generated is { } generated && nowUtc - generated < HeartbeatAge)
                {
                    return new WriteResult(WriteOutcome.Unchanged);
                }

                WriteAtomic(path, Build(targetName, paths, nowUtc), createDirectory);
                return new WriteResult(WriteOutcome.Heartbeat);
            }

            WriteAtomic(path, Build(targetName, paths, nowUtc), createDirectory);
            return new WriteResult(WriteOutcome.Written);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            return new WriteResult(WriteOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Removes a file the plugin wrote earlier, but only if it still carries the marker.
    /// </summary>
    /// <remarks>
    /// When the delete fails, usually because the folder is read-only, the file is overwritten in
    /// place with an empty list. Writing into an existing file needs only the file's permission,
    /// not the folder's, so this works where the atomic temp-file write cannot. An empty list tells
    /// the encoder nothing is urgent, which is the truth once the feature or target is gone.
    /// </remarks>
    /// <param name="path">The file.</param>
    /// <returns>What happened.</returns>
    public static RemoveOutcome Remove(string path)
    {
        ExistingFile existing;
        try
        {
            existing = Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RemoveOutcome.Failed;
        }

        if (!existing.Exists)
        {
            return RemoveOutcome.Missing;
        }

        if (!existing.IsOurs)
        {
            return RemoveOutcome.NotOurs;
        }

        try
        {
            File.Delete(path);
            return RemoveOutcome.Deleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall through to the empty list.
        }

        if (existing.Paths.Count == 0)
        {
            // Already empty: rewriting it would only move the mtime and make the encoder reload.
            return RemoveOutcome.Emptied;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, new PriorityFile { Producer = Producer, Paths = new List<string>() }, EmptyJsonOptions);
            stream.Flush(true);
            return RemoveOutcome.Emptied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RemoveOutcome.Failed;
        }
    }

    /// <summary>Reads what is at a path, without judging it beyond "ours or not".</summary>
    /// <remarks>
    /// Only a regular, non-empty file of a plausible size is opened. The output folder may be
    /// writable by others, and opening a named pipe blocks until something writes to it, which a
    /// cancellation token cannot interrupt. A pipe, a device and a symlink all read as foreign,
    /// so they are never opened, overwritten or deleted. A list the plugin wrote is never empty,
    /// so an empty regular file reads as foreign too, as it did when it failed to parse.
    /// </remarks>
    /// <param name="path">The file.</param>
    /// <returns>What is there.</returns>
    public static ExistingFile Read(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            return ExistingFile.Foreign;
        }

        if (!info.Exists)
        {
            return ExistingFile.None;
        }

        // A pipe or a device reports a length of zero.
        if (info.Length == 0 || info.Length > MaxFileBytes)
        {
            return ExistingFile.Foreign;
        }

        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            var file = JsonSerializer.Deserialize<PriorityFile>(bytes);
            if (file is null || !string.Equals(file.Producer, Producer, StringComparison.Ordinal))
            {
                return ExistingFile.Foreign;
            }

            DateTime? generated = DateTime.TryParse(
                file.Generated,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed
                : null;
            return new ExistingFile(true, true, file.Paths ?? new List<string>(), generated);
        }
        catch (JsonException)
        {
            return ExistingFile.Foreign;
        }
    }

    /// <summary>Writes bytes atomically: a dot-named temp file in the same folder, then a move over the target.</summary>
    /// <param name="path">The target file.</param>
    /// <param name="value">The object to serialise.</param>
    /// <param name="createDirectory">Whether a missing folder may be created.</param>
    internal static void WriteJsonAtomic<T>(string path, T value, bool createDirectory)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new IOException($"{path} has no folder");
        }

        if (!Directory.Exists(directory))
        {
            if (!createDirectory)
            {
                throw new DirectoryNotFoundException($"the folder {directory} does not exist; the plugin does not create folders there");
            }

            Directory.CreateDirectory(directory);
        }

        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + ".tmp");
        try
        {
            // The output folder may be writable by others (a library root), so a symlink may be
            // waiting at the temp name. FileMode.Create would follow it and overwrite whatever it
            // points at. Delete removes a link itself, never its target, and CreateNew (O_EXCL)
            // refuses anything planted again between the two calls.
            File.Delete(temp);

            // A FileStream straight into System.Text.Json is UTF-8 with no byte order mark.
            // A StreamWriter with Encoding.UTF8 would emit one, and the encoder cannot read it.
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static readonly JsonSerializerOptions EmptyJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static PriorityFile Build(string targetName, IReadOnlyList<string> paths, DateTime nowUtc) => new()
    {
        Generated = nowUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        Producer = Producer,
        Target = targetName,
        Paths = paths.ToList(),
    };

    private static void WriteAtomic(string path, PriorityFile file, bool createDirectory)
        => WriteJsonAtomic(path, file, createDirectory);

    private static void TryDelete(string path)
    {
        try
        {
            // File.Delete is a no-op for a missing file and removes a symlink, not its target.
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do; the target file was never touched.
        }
    }
}

/// <summary>What sits at an output path.</summary>
/// <param name="Exists">Whether a file is there.</param>
/// <param name="IsOurs">Whether it carries the plugin's marker.</param>
/// <param name="Paths">Its entries, when it is ours.</param>
/// <param name="Generated">When it was built, when it is ours and says so.</param>
internal sealed record ExistingFile(bool Exists, bool IsOurs, IReadOnlyList<string> Paths, DateTime? Generated)
{
    /// <summary>Gets the value for a path with no file.</summary>
    public static ExistingFile None { get; } = new(false, false, Array.Empty<string>(), null);

    /// <summary>Gets the value for a file the plugin did not write.</summary>
    public static ExistingFile Foreign { get; } = new(true, false, Array.Empty<string>(), null);
}
