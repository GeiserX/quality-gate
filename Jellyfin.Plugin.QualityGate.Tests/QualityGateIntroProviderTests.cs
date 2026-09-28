using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Providers;
using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.QualityGate.Tests;

[Collection(PluginInstanceCollection.Name)]
public class QualityGateIntroProviderTests : IDisposable
{
    private readonly Plugin _plugin;
    private readonly string _tempDir;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<IMediaSourceManager> _mediaSourceManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly QualityGateIntroProvider _provider;

    public QualityGateIntroProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qg-intro-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.SetReturnsDefault<string>(_tempDir);
        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(new PluginConfiguration());
        _plugin = new Plugin(appPaths.Object, xmlSerializer.Object);

        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerFactoryMock.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(Mock.Of<ILogger>());

        _libraryManagerMock = new Mock<ILibraryManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        _mediaSourceManagerMock = new Mock<IMediaSourceManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();

        _provider = new QualityGateIntroProvider(
            _loggerFactoryMock.Object,
            _libraryManagerMock.Object,
            _fileSystemMock.Object,
            _mediaSourceManagerMock.Object,
            _userDataManagerMock.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }

        GC.SuppressFinalize(this);
    }

    private void SetConfig(PluginConfiguration config)
    {
        _plugin.Configuration.Policies = config.Policies;
        _plugin.Configuration.UserPolicies = config.UserPolicies;
        _plugin.Configuration.DefaultPolicyId = config.DefaultPolicyId;
        _plugin.Configuration.DefaultIntroVideoPath = config.DefaultIntroVideoPath;
    }

    private static QualityPolicy MakePolicy(
        string id = "p1",
        List<string>? allowedPatterns = null,
        bool fallbackTranscode = false,
        string introVideoPath = "")
    {
        return new QualityPolicy
        {
            Id = id,
            Name = "Test Policy",
            Enabled = true,
            AllowedFilenamePatterns = allowedPatterns ?? new List<string>(),
            BlockedFilenamePatterns = new List<string>(),
            FallbackTranscode = fallbackTranscode,
            IntroVideoPath = introVideoPath,
        };
    }

    /// <summary>
    /// Sets up mocks so EnsureIntroRegistered succeeds: ResolvePath returns a real
    /// Video, and GetItemById returns the same Video (simulating "already in DB").
    /// </summary>
    private void SetupIntroRegistration(string introPath)
    {
        var video = new Video { Path = introPath };
        var mockFileInfo = new Mock<FileSystemMetadata>();
        _fileSystemMock.Setup(f => f.GetFileSystemInfo(introPath)).Returns(mockFileInfo.Object);
        _libraryManagerMock.Setup(l => l.ResolvePath(
            It.IsAny<FileSystemMetadata>(),
            It.IsAny<Folder>(),
            It.IsAny<IDirectoryService>()))
            .Returns(video);
        // Return the same video as "already exists in DB"
        _libraryManagerMock.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns(video);
    }

    /// <summary>
    /// Sets up mocks so EnsureIntroRegistered succeeds but the item is NOT in DB,
    /// forcing CreateItem to be called.
    /// </summary>
    private void SetupIntroRegistrationNotInDb(string introPath)
    {
        var video = new Video { Path = introPath };
        var mockFileInfo = new Mock<FileSystemMetadata>();
        _fileSystemMock.Setup(f => f.GetFileSystemInfo(introPath)).Returns(mockFileInfo.Object);
        _libraryManagerMock.Setup(l => l.ResolvePath(
            It.IsAny<FileSystemMetadata>(),
            It.IsAny<Folder>(),
            It.IsAny<IDirectoryService>()))
            .Returns(video);
        // Not in DB
        _libraryManagerMock.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((BaseItem?)null);
    }

    [Fact]
    public void Name_IsExpected()
    {
        Assert.Equal("QualityGate Intros", _provider.Name);
    }

    [Fact]
    public async Task GetIntros_NullUser_ReturnsEmpty()
    {
        var item = new Mock<BaseItem>();
        var result = await _provider.GetIntros(item.Object, null!);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetIntros_NoConfig_ReturnsEmpty()
    {
        SetConfig(new PluginConfiguration());

        var item = new Mock<BaseItem>();
        var user = new User("testuser", "default", "default");

        var result = await _provider.GetIntros(item.Object, user);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetIntros_NoIntroConfigured_ReturnsEmpty()
    {
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy() },
            DefaultPolicyId = "p1",
        });

        var item = new Mock<BaseItem>();
        var user = new User("testuser", "default", "default");

        var result = await _provider.GetIntros(item.Object, user);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetIntros_DefaultIntro_FileNotFound_ReturnsEmpty()
    {
        SetConfig(new PluginConfiguration
        {
            DefaultIntroVideoPath = "/nonexistent/intro.mp4",
        });

        var item = new Mock<BaseItem>();
        var user = new User("testuser", "default", "default");

        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);

        var result = await _provider.GetIntros(item.Object, user);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetIntros_PolicyIntro_FileNotFound_FallsBackToDefault()
    {
        var defaultIntro = Path.Combine(_tempDir, "default-intro.mp4");
        File.WriteAllText(defaultIntro, "intro");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(introVideoPath: "/nonexistent/policy-intro.mp4"),
            },
            DefaultPolicyId = "p1",
            DefaultIntroVideoPath = defaultIntro,
        });

        var item = new Mock<BaseItem>();
        var user = new User("testuser", "default", "default");

        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);

        // ResolvePath returns null (not a Video) → registration fails → returns empty
        var mockFileInfo = new Mock<FileSystemMetadata>();
        _fileSystemMock.Setup(f => f.GetFileSystemInfo(defaultIntro)).Returns(mockFileInfo.Object);
        _libraryManagerMock.Setup(l => l.ResolvePath(
            It.IsAny<FileSystemMetadata>(),
            It.IsAny<Folder>(),
            It.IsAny<IDirectoryService>()))
            .Returns((BaseItem?)null);

        var result = await _provider.GetIntros(item.Object, user);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetIntros_UserResuming_SkipsIntro()
    {
        var introPath = Path.Combine(_tempDir, "intro-resume.mp4");
        File.WriteAllText(introPath, "intro");

        SetConfig(new PluginConfiguration
        {
            DefaultIntroVideoPath = introPath,
        });

        var item = new Mock<BaseItem>();
        var user = new User("testuser", "default", "default");

        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test-key", PlaybackPositionTicks = 12345 });

        var result = await _provider.GetIntros(item.Object, user);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetIntros_UserAlreadyWatched_SkipsIntro()
    {
        var introPath = Path.Combine(_tempDir, "intro-watched.mp4");
        File.WriteAllText(introPath, "intro");

        SetConfig(new PluginConfiguration
        {
            DefaultIntroVideoPath = introPath,
        });

        var item = new Mock<BaseItem>();
        var user = new User("testuser", "default", "default");

        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test-key", Played = true });

        var result = await _provider.GetIntros(item.Object, user);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetIntros_AllSourcesBlocked_NoFallback_SkipsIntro()
    {
        var introPath = Path.Combine(_tempDir, "intro-blocked.mp4");
        File.WriteAllText(introPath, "intro");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }),
            },
            DefaultPolicyId = "p1",
            DefaultIntroVideoPath = introPath,
        });

        var item = new Mock<BaseItem>();
        var user = new User("testuser", "default", "default");

        _mediaSourceManagerMock.Setup(m => m.GetStaticMediaSources(
            It.IsAny<BaseItem>(), It.IsAny<bool>(), It.IsAny<User>()))
            .Returns(new List<MediaSourceInfo>
            {
                new MediaSourceInfo { Path = "/nonexistent/Movie - 4K.mkv" },
            });

        var result = await _provider.GetIntros(item.Object, user);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetIntros_PolicyThatDoesNotResolve_SkipsIntro_EvenWhenTheItemListsNoSources()
    {
        // The filter refuses every request from this user, so an intro would play and then fail.
        // An item with no static sources used to slip past the all-sources-blocked check above.
        ConfigureDefaultIntro();
        var user = new User("testuser", "default", "default");
        var assignment = new UserPolicyAssignment { UserId = user.Id, PolicyId = "deleted-policy" };
        _plugin.Configuration.UserPolicies = new List<UserPolicyAssignment> { assignment };
        _mediaSourceManagerMock.Setup(m => m.GetStaticMediaSources(
            It.IsAny<BaseItem>(), It.IsAny<bool>(), It.IsAny<User>()))
            .Returns(new List<MediaSourceInfo>());
        var item = new Mock<BaseItem>();

        Assert.Empty(await _provider.GetIntros(item.Object, user));

        // Reverse control: the same user and item on full access get the default intro.
        assignment.PolicyId = UserPolicyAssignment.FullAccessPolicyId;
        Assert.Single(await _provider.GetIntros(item.Object, user));
    }

    private string ConfigureDefaultIntro()
    {
        var introPath = Path.Combine(_tempDir, "intro-series.mp4");
        File.WriteAllText(introPath, "intro");
        SetConfig(new PluginConfiguration { DefaultIntroVideoPath = introPath });
        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);
        SetupIntroRegistration(introPath);
        return introPath;
    }

    private static Episode MakeEpisode(string showKey, int index, Guid? seriesId = null) => new Episode
    {
        Id = Guid.NewGuid(),
        Name = $"Episode {index}",
        IndexNumber = index,
        SeriesId = seriesId ?? Guid.NewGuid(),
        SeriesPresentationUniqueKey = showKey,
    };

    private static string NewShowKey() => $"{Guid.NewGuid():N}-es-lib";

    private static bool AsksForEpisodesOfShow(InternalItemsQuery q, string showKey) =>
        q.SeriesPresentationUniqueKey == showKey
        && q.IncludeItemTypes.Contains(BaseItemKind.Episode)
        && q.Recursive
        && q.Limit == 1;

    [Fact]
    public async Task GetIntros_Episode_ShowWithAPlayedEpisode_SkipsIntro_WithoutAnyCache()
    {
        // A fresh process, nothing in the session cache: this is the state after a server
        // restart, which used to replay the intro for every show.
        ConfigureDefaultIntro();
        var show = NewShowKey();
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => q.IsPlayed == true && AsksForEpisodesOfShow(q, show))))
            .Returns(new List<BaseItem> { MakeEpisode(show, 1) });
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IsResumable == true)))
            .Returns(new List<BaseItem>());

        var result = await _provider.GetIntros(MakeEpisode(show, 7), new User("s1", "default", "default"));

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetIntros_Episode_ShowLeftMidway_SkipsIntro()
    {
        ConfigureDefaultIntro();
        var show = NewShowKey();
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IsPlayed == true)))
            .Returns(new List<BaseItem>());
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => q.IsResumable == true && AsksForEpisodesOfShow(q, show))))
            .Returns(new List<BaseItem> { MakeEpisode(show, 3) });

        var result = await _provider.GetIntros(MakeEpisode(show, 4), new User("s2", "default", "default"));

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetIntros_Episode_ShowNeverWatched_ShowsIntroOnce_ThenSkipsEveryOtherEpisode()
    {
        ConfigureDefaultIntro();
        var show = NewShowKey();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());
        var user = new User("s3", "default", "default");

        var first = (await _provider.GetIntros(MakeEpisode(show, 1), user)).ToList();
        var second = (await _provider.GetIntros(MakeEpisode(show, 2), user)).ToList();

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public async Task GetIntros_Episode_SeasonFoldersAreOneShow()
    {
        // One folder per season at the library root gives one Series item per folder, so the
        // episodes of one show carry different SeriesIds. They share a presentation key, and
        // that is what "once per show" must key on.
        ConfigureDefaultIntro();
        var show = NewShowKey();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());
        var user = new User("s5", "default", "default");

        var season1 = (await _provider.GetIntros(MakeEpisode(show, 1, Guid.NewGuid()), user)).ToList();
        var season2 = (await _provider.GetIntros(MakeEpisode(show, 1, Guid.NewGuid()), user)).ToList();

        Assert.Single(season1);
        Assert.Empty(season2);
    }

    [Fact]
    public async Task GetIntros_Episode_WithoutAPresentationKey_FallsBackToTheSeriesId()
    {
        ConfigureDefaultIntro();
        var seriesId = Guid.NewGuid();
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => q.IsPlayed == true && q.AncestorIds.Contains(seriesId)
                     && string.IsNullOrEmpty(q.SeriesPresentationUniqueKey))))
            .Returns(new List<BaseItem> { new Episode { Id = Guid.NewGuid(), SeriesId = seriesId } });
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IsResumable == true)))
            .Returns(new List<BaseItem>());

        var episode = new Episode { Id = Guid.NewGuid(), IndexNumber = 2, SeriesId = seriesId };
        var result = await _provider.GetIntros(episode, new User("s6", "default", "default"));

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetIntros_Episode_WithoutAnyShowIdentity_GetsTheIntro_AndSilencesNothingElse()
    {
        ConfigureDefaultIntro();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());
        var user = new User("s7", "default", "default");

        var first = (await _provider.GetIntros(new Episode { Id = Guid.NewGuid(), IndexNumber = 1 }, user)).ToList();
        var second = (await _provider.GetIntros(new Episode { Id = Guid.NewGuid(), IndexNumber = 2 }, user)).ToList();

        Assert.Single(first);
        Assert.Single(second);
        _libraryManagerMock.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task GetIntros_Episode_NeverConsultsTheSeriesRowUserData()
    {
        // Jellyfin never stamps LastPlayedDate on a Series row, so a check there is a check
        // that cannot pass. The decision must come from episodes.
        ConfigureDefaultIntro();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        await _provider.GetIntros(MakeEpisode(NewShowKey(), 1), new User("s4", "default", "default"));

        _userDataManagerMock.Verify(u => u.GetUserData(It.IsAny<User>(), It.IsAny<Series>()), Times.Never);
    }

    [Fact]
    public async Task GetIntros_DefaultIntro_RegisterSucceeds_ReturnsIntro()
    {
        var introPath = Path.Combine(_tempDir, "intro-ok.mp4");
        File.WriteAllText(introPath, "intro");

        SetConfig(new PluginConfiguration
        {
            DefaultIntroVideoPath = introPath,
        });

        var item = new Mock<BaseItem>();
        var user = new User("testuser", "default", "default");

        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);

        SetupIntroRegistration(introPath);

        var result = (await _provider.GetIntros(item.Object, user)).ToList();
        Assert.Single(result);
    }

    [Fact]
    public async Task GetIntros_PolicyIntro_UsedOverDefault()
    {
        var policyIntro = Path.Combine(_tempDir, "policy-intro-ok.mp4");
        File.WriteAllText(policyIntro, "policy-intro");
        var defaultIntro = Path.Combine(_tempDir, "default-intro-ok.mp4");
        File.WriteAllText(defaultIntro, "default-intro");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(introVideoPath: policyIntro),
            },
            DefaultPolicyId = "p1",
            DefaultIntroVideoPath = defaultIntro,
        });

        var item = new Mock<BaseItem>();
        var user = new User("testuser", "default", "default");

        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);

        SetupIntroRegistration(policyIntro);

        var result = (await _provider.GetIntros(item.Object, user)).ToList();
        Assert.Single(result);
    }

    [Fact]
    public async Task GetIntros_EnsureIntroRegistered_CreatesItem_WhenNotInDb()
    {
        var introPath = Path.Combine(_tempDir, "new-intro.mp4");
        File.WriteAllText(introPath, "new-intro");

        SetConfig(new PluginConfiguration
        {
            DefaultIntroVideoPath = introPath,
        });

        var item = new Mock<BaseItem>();
        var user = new User("testuser", "default", "default");

        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);

        SetupIntroRegistrationNotInDb(introPath);

        var result = (await _provider.GetIntros(item.Object, user)).ToList();
        Assert.Single(result);
        _libraryManagerMock.Verify(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()), Times.Once);
    }

    [Fact]
    public async Task GetIntros_EnsureIntroRegistered_ResolvePathFails_ReturnsEmpty()
    {
        var introPath = Path.Combine(_tempDir, "broken-intro.mp4");
        File.WriteAllText(introPath, "broken");

        SetConfig(new PluginConfiguration
        {
            DefaultIntroVideoPath = introPath,
        });

        var item = new Mock<BaseItem>();
        var user = new User("testuser", "default", "default");

        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);

        var mockFileInfo = new Mock<FileSystemMetadata>();
        _fileSystemMock.Setup(f => f.GetFileSystemInfo(introPath)).Returns(mockFileInfo.Object);
        _libraryManagerMock.Setup(l => l.ResolvePath(
            It.IsAny<FileSystemMetadata>(),
            It.IsAny<Folder>(),
            It.IsAny<IDirectoryService>()))
            .Throws(new Exception("resolve failed"));

        var result = await _provider.GetIntros(item.Object, user);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetIntros_ShouldSkipIntro_ExceptionInUserData_ShowsIntro()
    {
        var introPath = Path.Combine(_tempDir, "intro-err.mp4");
        File.WriteAllText(introPath, "intro");

        SetConfig(new PluginConfiguration
        {
            DefaultIntroVideoPath = introPath,
        });

        var item = new Mock<BaseItem>();
        var user = new User("testuser", "default", "default");

        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Throws(new Exception("user data error"));

        SetupIntroRegistration(introPath);

        var result = (await _provider.GetIntros(item.Object, user)).ToList();
        Assert.Single(result);
    }

    [Fact]
    public async Task GetIntros_SourceCheckThrows_StillReturnsIntro()
    {
        var introPath = Path.Combine(_tempDir, "intro-src.mp4");
        File.WriteAllText(introPath, "intro");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }),
            },
            DefaultPolicyId = "p1",
            DefaultIntroVideoPath = introPath,
        });

        var item = new Mock<BaseItem>();
        var user = new User("testuser", "default", "default");

        _mediaSourceManagerMock.Setup(m => m.GetStaticMediaSources(
            It.IsAny<BaseItem>(), It.IsAny<bool>(), It.IsAny<User>()))
            .Throws(new Exception("source check error"));

        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);

        SetupIntroRegistration(introPath);

        var result = (await _provider.GetIntros(item.Object, user)).ToList();
        Assert.Single(result);
    }

    [Fact]
    public void IsRegisteredIntro_UnknownId_ReturnsFalse()
    {
        Assert.False(QualityGateIntroProvider.IsRegisteredIntro(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetIntros_CachedIntro_SecondCallUsesCache()
    {
        var introPath = Path.Combine(_tempDir, "cache-test-intro.mp4");
        File.WriteAllText(introPath, "intro");

        SetConfig(new PluginConfiguration
        {
            DefaultIntroVideoPath = introPath,
        });

        var item = new Mock<BaseItem>();
        var user = new User("testuser2", "default", "default");

        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);

        SetupIntroRegistration(introPath);

        var result1 = (await _provider.GetIntros(item.Object, user)).ToList();
        Assert.Single(result1);

        // Second call should use cache
        var result2 = (await _provider.GetIntros(item.Object, user)).ToList();
        Assert.Single(result2);
    }

    [Fact]
    public async Task GetIntros_FullAccessUser_WithDefaultIntro_ReturnsIntro()
    {
        var introPath = Path.Combine(_tempDir, "full-access-intro.mp4");
        File.WriteAllText(introPath, "intro");

        SetConfig(new PluginConfiguration
        {
            DefaultIntroVideoPath = introPath,
        });

        var item = new Mock<BaseItem>();
        var user = new User("fulluser", "default", "default");

        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);

        SetupIntroRegistration(introPath);

        var result = (await _provider.GetIntros(item.Object, user)).ToList();
        Assert.Single(result);
    }

    [Fact]
    public async Task GetIntros_ResolvePathReturnsNonVideo_ReturnsEmpty()
    {
        var introPath = Path.Combine(_tempDir, "non-video-intro.mp4");
        File.WriteAllText(introPath, "intro");

        SetConfig(new PluginConfiguration
        {
            DefaultIntroVideoPath = introPath,
        });

        var item = new Mock<BaseItem>();
        var user = new User("testuser", "default", "default");

        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);

        // Return a non-Video BaseItem
        var mockFileInfo = new Mock<FileSystemMetadata>();
        _fileSystemMock.Setup(f => f.GetFileSystemInfo(introPath)).Returns(mockFileInfo.Object);
        var mockBaseItem = new Mock<BaseItem>();
        _libraryManagerMock.Setup(l => l.ResolvePath(
            It.IsAny<FileSystemMetadata>(),
            It.IsAny<Folder>(),
            It.IsAny<IDirectoryService>()))
            .Returns(mockBaseItem.Object);

        var result = await _provider.GetIntros(item.Object, user);
        Assert.Empty(result);
    }
}
