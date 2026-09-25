using Jellyfin.Plugin.QualityGate.Library;

namespace Jellyfin.Plugin.QualityGate.Tests;

/// <summary>
/// The one rule for "is this file inside that folder", shared by version grouping and encode
/// priority. Both platforms are exercised explicitly so the Windows rules are covered on any host.
/// </summary>
public class PathRootsTests
{
    [Theory]
    [InlineData("/media/tv/Show A/Season 1/Show A S01E01.mkv", "/media/tv", "Show A/Season 1/Show A S01E01.mkv")]
    [InlineData("/media/tv/Show A/Season 1/Show A S01E01.mkv", "/media/tv/", "Show A/Season 1/Show A S01E01.mkv")]
    [InlineData("/media/tv/Show A/Season 1/Show A S01E01.mkv", "/media/tv///", "Show A/Season 1/Show A S01E01.mkv")]
    [InlineData("/media/tv", "/media/tv", "")]
    [InlineData("/media/tv/", "/media/tv", "")]
    public void Linux_MatchesTheRootAndBelowIt(string path, string root, string expected)
    {
        Assert.True(PathRoots.TryRelative(path, root, isWindows: false, out var remainder));
        Assert.Equal(expected, remainder);
    }

    [Theory]
    [InlineData("/media/tv2/Show A/x.mkv", "/media/tv")]
    [InlineData("/media/tvshows", "/media/tv")]
    [InlineData("/media", "/media/tv")]
    [InlineData("/other/tv/x.mkv", "/media/tv")]
    public void Linux_ASiblingThatSharesAPrefix_IsNotInside(string path, string root)
    {
        Assert.False(PathRoots.TryRelative(path, root, isWindows: false, out _));
    }

    [Fact]
    public void Linux_IsCaseSensitive()
    {
        Assert.False(PathRoots.TryRelative("/Media/TV/x.mkv", "/media/tv", isWindows: false, out _));
    }

    /// <summary>On Linux a backslash is part of a file name, so it neither separates nor matches a slash.</summary>
    [Fact]
    public void Linux_ABackslashIsAFilenameCharacter()
    {
        Assert.False(PathRoots.TryRelative(@"/media/tv\Show A/x.mkv", "/media/tv", isWindows: false, out _));
        Assert.True(PathRoots.TryRelative(@"/media/tv/Show\A.mkv", "/media/tv", isWindows: false, out var remainder));
        Assert.Equal(@"Show\A.mkv", remainder);
    }

    [Theory]
    [InlineData(@"D:\Media\TV\Show A\Season 1\x.mkv", @"D:\Media\TV", "Show A/Season 1/x.mkv")]
    [InlineData(@"D:\Media\TV\Show A\Season 1\x.mkv", "D:/Media/TV/", "Show A/Season 1/x.mkv")]
    [InlineData("D:/Media/TV/Show A/x.mkv", @"D:\Media\TV", "Show A/x.mkv")]
    [InlineData(@"d:\media\tv\Show A\x.mkv", @"D:\Media\TV", "Show A/x.mkv")]
    public void Windows_AcceptsBothSeparatorsAndIgnoresCase(string path, string root, string expected)
    {
        Assert.True(PathRoots.TryRelative(path, root, isWindows: true, out var remainder));
        Assert.Equal(expected, remainder);
    }

    [Fact]
    public void Windows_ASiblingThatSharesAPrefix_IsNotInside()
    {
        Assert.False(PathRoots.TryRelative(@"D:\Media\TV2\x.mkv", @"D:\Media\TV", isWindows: true, out _));
    }

    [Theory]
    [InlineData(null, "/media/tv")]
    [InlineData("", "/media/tv")]
    [InlineData("/media/tv/x.mkv", null)]
    [InlineData("/media/tv/x.mkv", "")]
    [InlineData("/media/tv/x.mkv", "/")]
    public void EmptyInputsAndABareSeparator_MatchNothing(string? path, string? root)
    {
        Assert.False(PathRoots.TryRelative(path, root, isWindows: false, out _));
    }

    [Fact]
    public void LongestMatch_PicksTheMostSpecificFolder()
    {
        var roots = new[] { "/media", "/media/tv/Show A", "/media/tv" };

        Assert.True(PathRoots.TryLongestMatch("/media/tv/Show A/Season 1/x.mkv", roots, isWindows: false, out var index, out var remainder));
        Assert.Equal(1, index);
        Assert.Equal("Season 1/x.mkv", remainder);
    }

    [Fact]
    public void LongestMatch_IgnoresASiblingPrefix()
    {
        var roots = new[] { "/media/tv", "/media/tv2" };

        Assert.True(PathRoots.TryLongestMatch("/media/tv2/Show B/x.mkv", roots, isWindows: false, out var index, out var remainder));
        Assert.Equal(1, index);
        Assert.Equal("Show B/x.mkv", remainder);
    }

    [Fact]
    public void LongestMatch_NoFolderContainsThePath()
    {
        Assert.False(PathRoots.TryLongestMatch("/srv/x.mkv", new[] { "/media/tv" }, isWindows: false, out var index, out _));
        Assert.Equal(-1, index);
    }

    [Fact]
    public void PlatformOverload_UsesThisHostsRules()
    {
        Assert.True(PathRoots.IsUnder(System.IO.Path.Combine("media", "tv", "x.mkv"), "media" + System.IO.Path.DirectorySeparatorChar + "tv"));
        Assert.False(PathRoots.IsUnder("/media/tv2/x.mkv", "/media/tv"));
    }
}
