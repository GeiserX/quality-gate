using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.EncodePriority;

namespace Jellyfin.Plugin.QualityGate.Tests;

public class EncodePriorityPathsTests
{
    private const string DataPath = "/config/data";
    private static readonly string[] Libraries = { "/media/tv", "/media/movies" };

    private static EncodeTargetOptions Target(string mode, string outputPath = "", params (string Jellyfin, string Encoder)[] folders)
    {
        var target = new EncodeTarget
        {
            Id = "0123456789abcdef",
            Name = "Shows",
            OutputMode = mode,
            OutputPath = outputPath,
        };
        foreach (var (jellyfin, encoder) in folders)
        {
            target.Folders.Add(new EncodeFolderMapping { JellyfinPath = jellyfin, EncoderPath = encoder });
        }

        return EncodePriorityOptions.From(new PluginConfiguration { EncodeTargets = new List<EncodeTarget> { target } }).Targets[0];
    }

    [Fact]
    public void SourceFolder_IsTheFolderWithAnEmptyEncoderPath()
    {
        var target = Target("SourceFolder", "", ("/media/movies", "movies"), ("/media/tv/", ""));

        Assert.True(EncodePriorityPaths.TryResolveOutput(target, DataPath, Libraries, out var path, out _));
        Assert.Equal(Path.Combine("/media/tv", ".encoder-priority.json"), path);
    }

    [Fact]
    public void SourceFolder_WithNoFolderAtTheEncoderRoot_HasNoOutput()
    {
        var target = Target("SourceFolder", "", ("/media/movies", "movies"), ("/media/tv", "tv"));

        Assert.False(EncodePriorityPaths.TryResolveOutput(target, DataPath, Libraries, out _, out var error));
        Assert.Contains("Data folder or Custom", error, StringComparison.Ordinal);
    }

    [Fact]
    public void DataFolder_UsesTheFirstEightCharactersOfTheId()
    {
        var target = Target("DataFolder", "", ("/media/tv", ""));

        Assert.True(EncodePriorityPaths.TryResolveOutput(target, DataPath, Libraries, out var path, out _));
        Assert.Equal(Path.Combine(DataPath, "quality-gate", "encode-priority", "01234567.json"), path);
        Assert.Equal(Path.Combine(DataPath, "quality-gate", "encode-priority", "state.json"), EncodePriorityPaths.StateFile(DataPath));
    }

    [Fact]
    public void DataFolder_AnIdWithPathCharacters_CannotEscapeTheFolder()
    {
        Assert.Equal(
            Path.Combine(DataPath, "quality-gate", "encode-priority", "______x.json"),
            EncodePriorityPaths.DataFolderFile(DataPath, "../../x"));
    }

    [Theory]
    [InlineData("/config/lists/shows.json", true)]
    [InlineData("/media/tv/.shows-priority.json", true)]
    [InlineData("/media/tv/shows-priority.json", false)]
    [InlineData("relative/shows.json", false)]
    [InlineData("", false)]
    public void Custom_MustBeAbsoluteAndHiddenInsideALibrary(string outputPath, bool valid)
    {
        var target = Target("Custom", outputPath, ("/media/tv", ""));

        Assert.Equal(valid, EncodePriorityPaths.TryResolveOutput(target, DataPath, Libraries, out var path, out var error));
        if (valid)
        {
            Assert.Equal(outputPath, path);
        }
        else
        {
            Assert.False(string.IsNullOrEmpty(error));
        }
    }
}
