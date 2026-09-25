using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.QualityGate.EncodePriority;

namespace Jellyfin.Plugin.QualityGate.Tests;

/// <summary>
/// The priority file on a real temporary folder: what the encoder reads, when it is rewritten,
/// and what the plugin refuses to touch.
/// </summary>
public sealed class PriorityFileWriterTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 25, 10, 12, 3, DateTimeKind.Utc);

    private readonly string _dir;

    public PriorityFileWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qg-pf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (var dir in Directory.GetDirectories(_dir, "*", SearchOption.AllDirectories).Prepend(_dir))
        {
            MakeWritable(dir);
        }

        Directory.Delete(_dir, true);
    }

    private string FilePath(string name = ".encoder-priority.json") => Path.Combine(_dir, name);

    private static readonly string[] TwoPaths =
    {
        "Show A/Season 2/Show A S02E05.mkv",
        "Show A/Season 2/Show A S02E06.mkv",
    };

    [Fact]
    public void Write_ProducesTheEncoderFormat_WithNoByteOrderMark()
    {
        var result = PriorityFileWriter.Write(FilePath(), "Shows", TwoPaths, createDirectory: false, Now);

        Assert.Equal(WriteOutcome.Written, result.Outcome);
        var bytes = File.ReadAllBytes(FilePath());
        Assert.Equal((byte)'{', bytes[0]);
        Assert.Equal(
            "{\"generated\":\"2026-09-25T10:12:03Z\",\"producer\":\"quality-gate\",\"target\":\"Shows\",\"paths\":[\"Show A/Season 2/Show A S02E05.mkv\",\"Show A/Season 2/Show A S02E06.mkv\"]}",
            Encoding.UTF8.GetString(bytes));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Write_KeepsNonAsciiPathsByteIdentical()
    {
        var paths = new[] { "Show É/Season 1/Show É S01E01.mkv" };

        PriorityFileWriter.Write(FilePath(), "Shows", paths, createDirectory: false, Now);

        using var doc = JsonDocument.Parse(File.ReadAllBytes(FilePath()));
        Assert.Equal(paths[0], doc.RootElement.GetProperty("paths")[0].GetString());
    }

    [Fact]
    public void Write_AnEmptyList_IsStillWritten()
    {
        var result = PriorityFileWriter.Write(FilePath(), "Shows", Array.Empty<string>(), createDirectory: false, Now);

        Assert.Equal(WriteOutcome.Written, result.Outcome);
        Assert.Empty(PriorityFileWriter.Read(FilePath()).Paths);
    }

    [Fact]
    public void Write_ASymlinkPlantedAtTheTempName_IsNotFollowed()
    {
        // The output folder may be writable by others. A link at the temp name must not make
        // the write overwrite the file it points at, nor end up as the output file.
        var victim = Path.Combine(_dir, "victim.db");
        File.WriteAllText(victim, "PRECIOUS");
        File.CreateSymbolicLink(Path.Combine(_dir, "..encoder-priority.json.tmp"), victim);

        var result = PriorityFileWriter.Write(FilePath(), "Shows", TwoPaths, createDirectory: false, Now);

        Assert.Equal(WriteOutcome.Written, result.Outcome);
        Assert.Equal("PRECIOUS", File.ReadAllText(victim));
        Assert.Null(new FileInfo(FilePath()).LinkTarget);
        Assert.Equal(TwoPaths, PriorityFileWriter.Read(FilePath()).Paths);
    }

    [Fact]
    public void Write_AnUnchangedList_LeavesTheFileAndItsMtimeAlone()
    {
        PriorityFileWriter.Write(FilePath(), "Shows", TwoPaths, createDirectory: false, Now);
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(FilePath(), stamp);

        var result = PriorityFileWriter.Write(FilePath(), "Shows", TwoPaths.ToList(), createDirectory: false, Now.AddHours(23));

        Assert.Equal(WriteOutcome.Unchanged, result.Outcome);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(FilePath()));
    }

    [Fact]
    public void Write_AChangedList_ReplacesTheFile()
    {
        PriorityFileWriter.Write(FilePath(), "Shows", TwoPaths, createDirectory: false, Now);

        var result = PriorityFileWriter.Write(FilePath(), "Shows", TwoPaths.Reverse().ToList(), createDirectory: false, Now.AddMinutes(5));

        Assert.Equal(WriteOutcome.Written, result.Outcome);
        Assert.Equal(TwoPaths.Reverse(), PriorityFileWriter.Read(FilePath()).Paths);
    }

    [Fact]
    public void Write_AnUnchangedListOlderThanADay_IsRewrittenWithAFreshTimestamp()
    {
        PriorityFileWriter.Write(FilePath(), "Shows", TwoPaths, createDirectory: false, Now);

        var result = PriorityFileWriter.Write(FilePath(), "Shows", TwoPaths, createDirectory: false, Now.AddHours(25));

        Assert.Equal(WriteOutcome.Heartbeat, result.Outcome);
        Assert.Equal(Now.AddHours(25), PriorityFileWriter.Read(FilePath()).Generated);
    }

    [Theory]
    [InlineData("Show A/x.mkv\n")]
    [InlineData("{\"paths\":[\"Show A/x.mkv\"]}")]
    [InlineData("{\"producer\":\"someone-else\",\"paths\":[]}")]
    [InlineData("[\"Show A/x.mkv\"]")]
    public void Write_RefusesAFileThePluginDidNotWrite(string contents)
    {
        File.WriteAllText(FilePath(), contents);

        var result = PriorityFileWriter.Write(FilePath(), "Shows", TwoPaths, createDirectory: false, Now);

        Assert.Equal(WriteOutcome.Foreign, result.Outcome);
        Assert.Contains(FilePath(), result.Error, StringComparison.Ordinal);
        Assert.Equal(contents, File.ReadAllText(FilePath()));
    }

    [Fact]
    public void Write_IntoAReadOnlyFolder_FailsAndKeepsThePreviousFile()
    {
        PriorityFileWriter.Write(FilePath(), "Shows", TwoPaths, createDirectory: false, Now);
        var before = File.ReadAllText(FilePath());
        MakeReadOnly(_dir);

        var result = PriorityFileWriter.Write(FilePath(), "Shows", new[] { "Show B/x.mkv" }, createDirectory: false, Now.AddMinutes(1));

        MakeWritable(_dir);
        Assert.Equal(WriteOutcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Equal(before, File.ReadAllText(FilePath()));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Write_IntoAMissingFolder_FailsUnlessItMayCreateIt()
    {
        var nested = Path.Combine(_dir, "quality-gate", "encode-priority", "abcd1234.json");

        var refused = PriorityFileWriter.Write(nested, "Shows", TwoPaths, createDirectory: false, Now);
        Assert.Equal(WriteOutcome.Failed, refused.Outcome);
        Assert.False(Directory.Exists(Path.GetDirectoryName(nested)));

        var created = PriorityFileWriter.Write(nested, "Shows", TwoPaths, createDirectory: true, Now);
        Assert.Equal(WriteOutcome.Written, created.Outcome);
        Assert.Equal(TwoPaths, PriorityFileWriter.Read(nested).Paths);
    }

    [Fact]
    public void Cleanup_RemovesOnlyUnclaimedFilesThePluginWrote()
    {
        var kept = FilePath("kept.json");
        var dropped = FilePath("dropped.json");
        var foreign = FilePath("foreign.json");
        var gone = FilePath("gone.json");
        PriorityFileWriter.Write(kept, "A", TwoPaths, false, Now);
        PriorityFileWriter.Write(dropped, "B", TwoPaths, false, Now);
        File.WriteAllText(foreign, "{\"paths\":[\"by hand\"]}");
        var state = new EncodePriorityState { WrittenFiles = new List<string> { kept, dropped, foreign, gone } };

        var results = CleanupPass.Run(state, new HashSet<string> { kept });

        Assert.True(File.Exists(kept));
        Assert.False(File.Exists(dropped));
        Assert.Equal("{\"paths\":[\"by hand\"]}", File.ReadAllText(foreign));
        Assert.Equal(new[] { kept }, state.WrittenFiles);
        Assert.Equal(
            new[] { (dropped, RemoveOutcome.Deleted), (foreign, RemoveOutcome.NotOurs), (gone, RemoveOutcome.Missing) },
            results);
    }

    [Fact]
    public void Cleanup_WhenTheDeleteFails_WritesAnEmptyListAndKeepsTheRecord()
    {
        var file = FilePath();
        PriorityFileWriter.Write(file, "Shows", TwoPaths, false, Now);
        var state = new EncodePriorityState { WrittenFiles = new List<string> { file } };
        MakeReadOnly(_dir);

        var first = CleanupPass.Run(state, new HashSet<string>());
        var stamp = File.GetLastWriteTimeUtc(file);
        var second = CleanupPass.Run(state, new HashSet<string>());

        MakeWritable(_dir);
        Assert.Equal(RemoveOutcome.Emptied, Assert.Single(first).Outcome);
        Assert.Equal(RemoveOutcome.Emptied, Assert.Single(second).Outcome);
        var emptied = PriorityFileWriter.Read(file);
        Assert.True(emptied.IsOurs);
        Assert.Empty(emptied.Paths);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
        Assert.Equal(new[] { file }, state.WrittenFiles);
    }

    [Fact]
    public void State_RoundTripsAndStartsEmptyWhenMissingOrCorrupt()
    {
        var path = Path.Combine(_dir, "quality-gate", "encode-priority", "state.json");
        Assert.Empty(EncodePriorityState.Load(path, out var missingError).WrittenFiles);
        Assert.Null(missingError);

        var state = new EncodePriorityState();
        state.RecordWritten("/media/tv/.encoder-priority.json");
        state.RecordWritten("/media/tv/.encoder-priority.json");
        state.Save(path);

        Assert.Equal(new[] { "/media/tv/.encoder-priority.json" }, EncodePriorityState.Load(path, out _).WrittenFiles);

        File.WriteAllText(path, "not json");
        Assert.Empty(EncodePriorityState.Load(path, out var corruptError).WrittenFiles);
        Assert.NotNull(corruptError);
    }

    private static void MakeReadOnly(string dir)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    private static void MakeWritable(string dir)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
