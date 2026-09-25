using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.Naming.Common;
using Emby.Naming.Video;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Library;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.QualityGate.Tests;

[Collection(PluginInstanceCollection.Name)]
public class VersionGroupingResolverTests : IDisposable
{
    private readonly Plugin _plugin;
    private readonly string _tempDir;
    private readonly VersionGroupingResolver _resolver;

    public VersionGroupingResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qg-vg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.SetReturnsDefault<string>(_tempDir);
        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(new PluginConfiguration());
        _plugin = new Plugin(appPaths.Object, xmlSerializer.Object);

        var namingOptions = new NamingOptions();
        _resolver = new VersionGroupingResolver(
            namingOptions,
            new VideoListResolver(namingOptions),
            NullLogger<VersionGroupingResolver>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }

        GC.SuppressFinalize(this);
    }

    private void Enable(params string[] roots)
    {
        _plugin.Configuration.EnableVersionGrouping = true;
        _plugin.Configuration.VersionGroupingSuffixes = new List<string> { " - 720p" };
        _plugin.Configuration.VersionGroupingRoots = roots.ToList();
    }

    /// <summary>Creates real files, because the resolver is asked about a real directory listing.</summary>
    private List<FileSystemMetadata> Files(params string[] names)
    {
        var list = new List<FileSystemMetadata>();
        foreach (var name in names)
        {
            var full = Path.Combine(_tempDir, name);
            File.WriteAllText(full, string.Empty);
            list.Add(new FileSystemMetadata { FullName = full, Name = name, IsDirectory = false });
        }

        return list;
    }

    private Folder Parent() => new Folder { Path = _tempDir };

    private MultiItemResolverResult? Resolve(List<FileSystemMetadata> files, CollectionType? type = CollectionType.movies)
        => _resolver.ResolveMultiple(Parent(), files, type, Mock.Of<IDirectoryService>());

    [Fact]
    public void Priority_IsPluginSoItRunsBeforeTheDefaultResolvers()
    {
        Assert.Equal(ResolverPriority.Plugin, _resolver.Priority);
    }

    [Fact]
    public void ResolvePath_AlwaysDeclines()
    {
        // Grouping is only meaningful across a listing, never for one file on its own.
        Assert.Null(_resolver.ResolvePath(null!));
    }

    [Fact]
    public void Disabled_ClaimsNothing()
    {
        // The default configuration must leave every library exactly as Jellyfin resolves it.
        var files = Files("Alien (1979).mkv", "Alien (1979) - 720p.mkv");
        Assert.Null(Resolve(files));
    }

    [Fact]
    public void NonMovieLibrary_ClaimsNothing()
    {
        Enable();
        var files = Files("Alien (1979).mkv", "Alien (1979) - 720p.mkv");
        Assert.Null(Resolve(files, CollectionType.tvshows));
    }

    [Fact]
    public void OutsideAConfiguredRoot_ClaimsNothing()
    {
        Enable("/some/other/library");
        var files = Files("Alien (1979).mkv", "Alien (1979) - 720p.mkv");
        Assert.Null(Resolve(files));
    }

    [Fact]
    public void NoSuffixesConfigured_PairsWithTheDefaultSuffix()
    {
        // An empty list is what a config that never set suffixes loads as, and a restarted
        // server has always paired " - 720p" copies for it.
        Enable();
        _plugin.Configuration.VersionGroupingSuffixes = new List<string>();
        var files = Files("Alien (1979).mkv", "Alien (1979) - 720p.mkv");
        var result = Resolve(files);
        Assert.NotNull(result);
        var movie = Assert.Single(result!.Items);
        Assert.Equal(Path.Combine(_tempDir, "Alien (1979).mkv"), movie.Path);
        var version = Assert.Single(((Video)movie).LocalAlternateVersions);
        Assert.Equal(Path.Combine(_tempDir, "Alien (1979) - 720p.mkv"), version);
    }

    [Fact]
    public void NothingToMerge_ClaimsNothing()
    {
        // Declining leaves stacking, extras and naming entirely to Jellyfin.
        Enable();
        var files = Files("Alien (1979).mkv", "Aliens (1986).mkv");
        Assert.Null(Resolve(files));
    }

    [Fact]
    public void DiscImage_HandsTheFolderBack()
    {
        // VideoType and IsoType need the file inspected, which is the server's job.
        Enable();
        var files = Files("Alien (1979).mkv", "Alien (1979) - 720p.mkv", "Blade Runner (1982).iso");
        Assert.Null(Resolve(files));
    }

    [Fact]
    public void SiblingCopy_BecomesAnAlternateVersionOfTheOriginal()
    {
        Enable(_tempDir);
        var files = Files("Alien (1979).mkv", "Alien (1979) - 720p.mkv");

        var result = Resolve(files);

        Assert.NotNull(result);
        var movie = Assert.Single(result!.Items);
        Assert.Equal(Path.Combine(_tempDir, "Alien (1979).mkv"), movie.Path);
        var version = Assert.Single(((Video)movie).LocalAlternateVersions);
        Assert.Equal(Path.Combine(_tempDir, "Alien (1979) - 720p.mkv"), version);
        Assert.Equal(1979, movie.ProductionYear);
        Assert.Empty(result.ExtraFiles);
    }

    [Fact]
    public void OtherFilmsInTheFolder_SurviveAsTheirOwnItems()
    {
        Enable();
        var files = Files(
            "Alien (1979).mkv",
            "Alien (1979) - 720p.mkv",
            "Aliens (1986).mkv",
            "Blade Runner (1982).mkv");

        var result = Resolve(files);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Items.Count);
        Assert.DoesNotContain(result.Items, i => i.Path.Contains(" - 720p", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryFileIsAccountedFor_NoneSilentlyDropped()
    {
        // A file that is neither an item nor an extra would simply vanish from the library.
        Enable();
        var names = new[] { "Alien (1979).mkv", "Alien (1979) - 720p.mkv", "Aliens (1986).mkv" };
        var files = Files(names);

        var result = Resolve(files);

        Assert.NotNull(result);
        var seen = result!.Items.SelectMany(i =>
                new[] { i.Path }
                    .Concat(((Video)i).LocalAlternateVersions)
                    .Concat(((Video)i).AdditionalParts))
            .Concat(result.ExtraFiles.Select(f => f.FullName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            Assert.Contains(Path.Combine(_tempDir, name), seen);
        }
    }

    [Fact]
    public void CopyInADifferentContainer_StillGroups()
    {
        Enable();
        var files = Files("Alien (1979).mkv", "Alien (1979) - 720p.mp4");

        var result = Resolve(files);

        Assert.NotNull(result);
        var movie = Assert.Single(result!.Items);
        Assert.EndsWith(".mkv", movie.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageSibling_IsNotSwallowed()
    {
        // Jellyfin's own folder-name rule would merge this one; ours must not.
        Enable();
        var files = Files("Alien (1979).mkv", "Alien (1979) - German.mkv");

        Assert.Null(Resolve(files));
    }

    [Fact]
    public void AMalformedEntry_DoesNotBreakTheScan()
    {
        // A throw during resolution would abort the library scan, so it has to degrade to "not mine".
        Enable();
        var files = Files("Alien (1979).mkv", "Alien (1979) - 720p.mkv");
        files.Add(new FileSystemMetadata { FullName = null!, Name = null!, IsDirectory = false });

        Assert.Null(Resolve(files));
    }

    [Fact]
    public void OnlyOneRealFileAmongTheEntries_ClaimsNothing()
    {
        Enable();
        var files = Files("Alien (1979).mkv");
        var sub = Path.Combine(_tempDir, "Some Folder");
        Directory.CreateDirectory(sub);
        files.Add(new FileSystemMetadata { FullName = sub, Name = "Some Folder", IsDirectory = true });

        Assert.Null(Resolve(files));
    }

    [Fact]
    public void SampleFiles_AreNotFilms()
    {
        // Jellyfin's own movie resolver drops these before resolving; so must this.
        Enable();
        var files = Files("Alien (1979).mkv", "Alien (1979) - 720p.mkv", "Alien (1979)-sample.mkv");

        var result = Resolve(files);

        Assert.NotNull(result);
        Assert.Single(result!.Items);
        Assert.DoesNotContain(result.Items, i => i.Path.Contains("sample", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoConfiguredRoots_MeansEveryMovieLibrary()
    {
        Enable();
        _plugin.Configuration.VersionGroupingRoots = null!;
        var files = Files("Alien (1979).mkv", "Alien (1979) - 720p.mkv");

        Assert.NotNull(Resolve(files));
    }

    [Fact]
    public void ARootWithATrailingSlash_StillMatches()
    {
        Enable(_tempDir + Path.DirectorySeparatorChar);
        var files = Files("Alien (1979).mkv", "Alien (1979) - 720p.mkv");

        Assert.NotNull(Resolve(files));
    }

    [Fact]
    public void ASiblingDirectoryWithASharedPrefix_IsNotInsideTheRoot()
    {
        // "/media/Pel" must not swallow "/media/Peliculas".
        Enable(_tempDir + "-other");
        var files = Files("Alien (1979).mkv", "Alien (1979) - 720p.mkv");

        Assert.Null(Resolve(files));
    }

    [Theory]
    [InlineData("hsbs", Video3DFormat.HalfSideBySide)]
    [InlineData("sbs", Video3DFormat.HalfSideBySide)]
    [InlineData("sbs3d", Video3DFormat.HalfSideBySide)]
    [InlineData("fsbs", Video3DFormat.FullSideBySide)]
    [InlineData("htab", Video3DFormat.HalfTopAndBottom)]
    [InlineData("tab", Video3DFormat.HalfTopAndBottom)]
    [InlineData("ftab", Video3DFormat.FullTopAndBottom)]
    [InlineData("mvc", Video3DFormat.MVC)]
    public void ThreeDFormat_IsCarriedOntoTheGroupedFilm(string token, Video3DFormat expected)
    {
        Enable();
        var files = Files(
            "Alien (1979) 3d " + token + ".mkv",
            "Alien (1979) 3d " + token + " - 720p.mkv");

        var result = Resolve(files);

        Assert.NotNull(result);
        var movie = Assert.Single(result!.Items);
        Assert.Equal(expected, ((Video)movie).Video3DFormat);
    }

    [Fact]
    public void AFilmWithNo3DMarking_HasNo3DFormat()
    {
        Enable();
        var files = Files("Alien (1979).mkv", "Alien (1979) - 720p.mkv");

        var result = Resolve(files);

        Assert.Null(((Video)Assert.Single(result!.Items)).Video3DFormat);
    }

    [Fact]
    public void ATrailerBesideTheFilm_IsAnExtraNotAFilm()
    {
        Enable();
        var files = Files("Alien (1979).mkv", "Alien (1979) - 720p.mkv", "Alien (1979)-trailer.mkv");

        var result = Resolve(files);

        Assert.NotNull(result);
        Assert.Single(result!.Items);
        Assert.Contains(result.ExtraFiles, f => f.Name.Contains("trailer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Directories_ArePassedThroughForNormalResolution()
    {
        Enable();
        var files = Files("Alien (1979).mkv", "Alien (1979) - 720p.mkv");
        var sub = Path.Combine(_tempDir, "Blade Runner (1982)");
        Directory.CreateDirectory(sub);
        files.Add(new FileSystemMetadata { FullName = sub, Name = "Blade Runner (1982)", IsDirectory = true });

        var result = Resolve(files);

        Assert.NotNull(result);
        Assert.Single(result!.Items);
        Assert.Contains(result.ExtraFiles, f => f.FullName == sub);
    }
}
