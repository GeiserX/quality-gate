using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Filters;
using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.QualityGate.Tests;

public class MediaSourceResultFilterTests : IDisposable
{
    private readonly Plugin _plugin;
    private readonly string _tempDir;
    private readonly Mock<ILogger<MediaSourceResultFilter>> _loggerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IMediaSourceManager> _mediaSourceManagerMock;
    private readonly MediaSourceResultFilter _filter;

    public MediaSourceResultFilterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qg-filter-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.SetReturnsDefault<string>(_tempDir);
        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(new PluginConfiguration());
        _plugin = new Plugin(appPaths.Object, xmlSerializer.Object);

        _loggerMock = new Mock<ILogger<MediaSourceResultFilter>>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _mediaSourceManagerMock = new Mock<IMediaSourceManager>();
        _filter = new MediaSourceResultFilter(
            _loggerMock.Object,
            _libraryManagerMock.Object,
            _mediaSourceManagerMock.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }

        GC.SuppressFinalize(this);
    }

    private string CreateTempFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "test");
        return path;
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
        List<string>? blockedPatterns = null,
        bool fallbackTranscode = false,
        string introVideoPath = "")
    {
        return new QualityPolicy
        {
            Id = id,
            Name = "Test Policy",
            Enabled = true,
            AllowedFilenamePatterns = allowedPatterns ?? new List<string>(),
            BlockedFilenamePatterns = blockedPatterns ?? new List<string>(),
            FallbackTranscode = fallbackTranscode,
            IntroVideoPath = introVideoPath,
        };
    }

    private static HttpContext CreateHttpContext(
        string path,
        string method = "GET",
        Guid? userId = null,
        Dictionary<string, string>? queryParams = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        httpContext.Request.Method = method;

        if (userId.HasValue)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()),
            };
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        if (queryParams != null)
        {
            var qc = new QueryCollection(queryParams.ToDictionary(
                kvp => kvp.Key,
                kvp => new Microsoft.Extensions.Primitives.StringValues(kvp.Value)));
            httpContext.Request.Query = qc;
        }

        return httpContext;
    }

    private static ResultExecutingContext CreateResultContext(
        HttpContext httpContext,
        object? resultValue = null)
    {
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var objectResult = resultValue != null
            ? new ObjectResult(resultValue)
            : new ObjectResult(null);

        return new ResultExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            objectResult,
            new object());
    }

    private static ResourceExecutingContext CreateResourceContext(
        HttpContext httpContext)
    {
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ResourceExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new List<IValueProviderFactory>());
    }

    // --- OnResultExecutionAsync tests ---

    [Fact]
    public async Task ResultFilter_IrrelevantPath_DoesNotFilter()
    {
        var userId = Guid.NewGuid();
        var httpContext = CreateHttpContext("/api/System/Info", userId: userId);

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var playbackInfo = new PlaybackInfoResponse
        {
            MediaSources = new[]
            {
                new MediaSourceInfo { Path = "/media/Movie - 1080p.mkv" },
            },
        };

        var context = CreateResultContext(httpContext, playbackInfo);
        var executed = false;
        Task<ResultExecutedContext> Next()
        {
            executed = true;
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);
        Assert.True(executed);
        // Sources should not be filtered since path is irrelevant
        Assert.Single(playbackInfo.MediaSources);
    }

    [Fact]
    public async Task ResultFilter_PlaybackInfo_FiltersBlockedSources()
    {
        var userId = Guid.NewGuid();
        var path720 = CreateTempFile("Movie - 720p.mkv");
        var path1080 = CreateTempFile("Movie - 1080p.mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Items/00000000-0000-0000-0000-000000000001/PlaybackInfo",
            userId: userId);

        var playbackInfo = new PlaybackInfoResponse
        {
            MediaSources = new[]
            {
                new MediaSourceInfo { Path = path720 },
                new MediaSourceInfo { Path = path1080 },
            },
        };

        var context = CreateResultContext(httpContext, playbackInfo);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        Assert.Single(playbackInfo.MediaSources);
        Assert.Equal(path720, playbackInfo.MediaSources[0].Path);
    }

    [Fact]
    public async Task ResultFilter_PlaybackInfo_AllBlocked_SetsNotAllowed()
    {
        var userId = Guid.NewGuid();

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Items/00000000-0000-0000-0000-000000000001/PlaybackInfo",
            userId: userId);

        var playbackInfo = new PlaybackInfoResponse
        {
            MediaSources = new[]
            {
                new MediaSourceInfo { Path = "/nonexistent/Movie - 1080p.mkv" },
            },
        };

        var context = CreateResultContext(httpContext, playbackInfo);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        Assert.Empty(playbackInfo.MediaSources);
        Assert.Equal(MediaBrowser.Model.Dlna.PlaybackErrorCode.NotAllowed, playbackInfo.ErrorCode);
    }

    [Fact]
    public async Task ResultFilter_PlaybackInfo_FallbackTranscode_WhenAllBlocked()
    {
        var userId = Guid.NewGuid();
        var pathRemux = CreateTempFile("Movie [BDRemux].mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true),
            },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Items/00000000-0000-0000-0000-000000000001/PlaybackInfo",
            userId: userId);

        var playbackInfo = new PlaybackInfoResponse
        {
            MediaSources = new[]
            {
                new MediaSourceInfo
                {
                    Path = pathRemux,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                },
            },
        };

        var context = CreateResultContext(httpContext, playbackInfo);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        Assert.Single(playbackInfo.MediaSources);
        Assert.False(playbackInfo.MediaSources[0].SupportsDirectPlay);
        Assert.False(playbackInfo.MediaSources[0].SupportsDirectStream);
    }

    [Fact]
    public async Task ResultFilter_PlaybackInfo_IntroPath_SkipsFiltering()
    {
        var userId = Guid.NewGuid();
        var introPath = "/media/intros/GeiserLand.mp4";

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, introVideoPath: introPath),
            },
            DefaultPolicyId = "p1",
            DefaultIntroVideoPath = introPath,
        });

        var httpContext = CreateHttpContext(
            "/Items/00000000-0000-0000-0000-000000000001/PlaybackInfo",
            userId: userId);

        var playbackInfo = new PlaybackInfoResponse
        {
            MediaSources = new[]
            {
                new MediaSourceInfo { Path = introPath },
            },
        };

        var context = CreateResultContext(httpContext, playbackInfo);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        // Intro video should not be filtered
        Assert.Single(playbackInfo.MediaSources);
    }

    [Fact]
    public async Task ResultFilter_BaseItemDto_FiltersSources()
    {
        var userId = Guid.NewGuid();
        var path720 = CreateTempFile("Movie - 720p.mkv");
        var path1080 = CreateTempFile("Movie - 1080p.mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Users/" + userId + "/Items/00000000-0000-0000-0000-000000000001",
            userId: userId);

        var itemDto = new BaseItemDto
        {
            MediaSources = new[]
            {
                new MediaSourceInfo { Path = path720 },
                new MediaSourceInfo { Path = path1080 },
            },
        };

        var context = CreateResultContext(httpContext, itemDto);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        Assert.Single(itemDto.MediaSources);
        Assert.Equal(path720, itemDto.MediaSources[0].Path);
    }

    [Fact]
    public async Task ResultFilter_BaseItemDto_FallbackTranscode_WhenAllBlocked()
    {
        var userId = Guid.NewGuid();
        var pathRemux = CreateTempFile("Movie [BDRemux].mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true),
            },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Users/" + userId + "/Items/00000000-0000-0000-0000-000000000001",
            userId: userId);

        var itemDto = new BaseItemDto
        {
            MediaSources = new[]
            {
                new MediaSourceInfo
                {
                    Path = pathRemux,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                },
            },
        };

        var context = CreateResultContext(httpContext, itemDto);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        Assert.Single(itemDto.MediaSources);
        Assert.False(itemDto.MediaSources[0].SupportsDirectPlay);
        Assert.False(itemDto.MediaSources[0].SupportsDirectStream);
    }

    [Fact]
    public async Task ResultFilter_QueryResult_HidesFullyBlockedItems()
    {
        var userId = Guid.NewGuid();
        var path720 = CreateTempFile("Movie - 720p.mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Users/" + userId + "/Items",
            userId: userId);

        var allowedItem = new BaseItemDto
        {
            Name = "Allowed Movie",
            MediaSources = new[]
            {
                new MediaSourceInfo { Path = path720 },
            },
        };

        var blockedItem = new BaseItemDto
        {
            Name = "Blocked Movie",
            MediaSources = new[]
            {
                new MediaSourceInfo { Path = CreateTempFile("Movie - 4K.mkv") },
            },
        };

        var queryResult = new QueryResult<BaseItemDto>
        {
            Items = new[] { allowedItem, blockedItem },
            TotalRecordCount = 2,
        };

        var context = CreateResultContext(httpContext, queryResult);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        Assert.Single(queryResult.Items);
        Assert.Equal("Allowed Movie", queryResult.Items[0].Name);
        Assert.Equal(1, queryResult.TotalRecordCount);
    }

    [Fact]
    public async Task ResultFilter_QueryResult_LooksUpLibrarySources_WhenMediaSourcesNull()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Users/" + userId + "/Items",
            userId: userId);

        // Item has no MediaSources populated — filter should look up from library
        var item = new BaseItemDto
        {
            Id = itemId,
            Name = "Library Item",
            MediaSources = null,
        };

        var mockBaseItem = new Mock<MediaBrowser.Controller.Entities.BaseItem>();
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(mockBaseItem.Object);
        _mediaSourceManagerMock.Setup(m => m.GetStaticMediaSources(It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(), It.IsAny<bool>(), It.IsAny<Jellyfin.Database.Implementations.Entities.User>()))
            .Returns(new List<MediaSourceInfo>
            {
                new MediaSourceInfo { Path = CreateTempFile("Movie - 1080p.mkv") },
            });

        var queryResult = new QueryResult<BaseItemDto>
        {
            Items = new[] { item },
            TotalRecordCount = 1,
        };

        var context = CreateResultContext(httpContext, queryResult);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        // Item should be hidden since its only source is 1080p (not 720p)
        Assert.Empty(queryResult.Items);
    }

    [Fact]
    public async Task ResultFilter_NoUserId_DoesNotFilter()
    {
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Items/00000000-0000-0000-0000-000000000001/PlaybackInfo");

        var playbackInfo = new PlaybackInfoResponse
        {
            MediaSources = new[]
            {
                new MediaSourceInfo { Path = "/media/Movie - 1080p.mkv" },
            },
        };

        var context = CreateResultContext(httpContext, playbackInfo);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        Assert.Single(playbackInfo.MediaSources);
    }

    [Fact]
    public async Task ResultFilter_NoPolicy_DoesNotFilter()
    {
        var userId = Guid.NewGuid();

        SetConfig(new PluginConfiguration()); // No policies

        var httpContext = CreateHttpContext(
            "/Items/00000000-0000-0000-0000-000000000001/PlaybackInfo",
            userId: userId);

        var playbackInfo = new PlaybackInfoResponse
        {
            MediaSources = new[]
            {
                new MediaSourceInfo { Path = "/media/Movie - 1080p.mkv" },
            },
        };

        var context = CreateResultContext(httpContext, playbackInfo);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        Assert.Single(playbackInfo.MediaSources);
    }

    [Fact]
    public async Task ResultFilter_ForcedTranscodeKey_SkipsFiltering()
    {
        var userId = Guid.NewGuid();

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Items/00000000-0000-0000-0000-000000000001/PlaybackInfo",
            userId: userId);

        // Simulate Phase 1 having set the forced transcode key
        httpContext.Items["QG_ForcedTranscode"] = true;

        var playbackInfo = new PlaybackInfoResponse
        {
            MediaSources = new[]
            {
                new MediaSourceInfo { Path = "/media/Movie - 1080p.mkv" },
            },
        };

        var context = CreateResultContext(httpContext, playbackInfo);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        // Forced transcode skips result filtering for PlaybackInfoResponse
        Assert.Single(playbackInfo.MediaSources);
    }

    [Fact]
    public async Task ResultFilter_NullResultValue_DoesNotThrow()
    {
        var userId = Guid.NewGuid();

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Items/00000000-0000-0000-0000-000000000001/PlaybackInfo",
            userId: userId);

        var context = CreateResultContext(httpContext, null);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);
        // Should not throw
    }

    // --- GetUserId extraction tests ---

    [Fact]
    public async Task ResultFilter_ExtractsUserId_FromQueryParam()
    {
        var userId = Guid.NewGuid();
        var path720 = CreateTempFile("Movie - 720p.mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Items/00000000-0000-0000-0000-000000000001/PlaybackInfo",
            queryParams: new Dictionary<string, string> { { "userId", userId.ToString() } });

        var playbackInfo = new PlaybackInfoResponse
        {
            MediaSources = new[]
            {
                new MediaSourceInfo { Path = path720 },
                new MediaSourceInfo { Path = CreateTempFile("Movie - 4K.mkv") },
            },
        };

        var context = CreateResultContext(httpContext, playbackInfo);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        Assert.Single(playbackInfo.MediaSources);
        Assert.Equal(path720, playbackInfo.MediaSources[0].Path);
    }

    [Fact]
    public async Task ResultFilter_ExtractsUserId_FromRouteValues()
    {
        var userId = Guid.NewGuid();
        var path720 = CreateTempFile("Movie - 720p.mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Users/" + userId + "/Items/00000000-0000-0000-0000-000000000001");
        httpContext.Request.RouteValues["userId"] = userId.ToString();

        var playbackInfo = new PlaybackInfoResponse
        {
            MediaSources = new[]
            {
                new MediaSourceInfo { Path = path720 },
                new MediaSourceInfo { Path = CreateTempFile("Movie - 4K.mkv") },
            },
        };

        var context = CreateResultContext(httpContext, playbackInfo);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        Assert.Single(playbackInfo.MediaSources);
    }

    [Fact]
    public async Task ResultFilter_ExtractsUserId_FromUrlPath()
    {
        var userId = Guid.NewGuid();
        var path720 = CreateTempFile("Movie - 720p.mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        // UserId is in the URL path itself
        var httpContext = CreateHttpContext(
            "/Users/" + userId + "/Items");

        var queryResult = new QueryResult<BaseItemDto>
        {
            Items = new[]
            {
                new BaseItemDto
                {
                    Name = "Good Movie",
                    MediaSources = new[] { new MediaSourceInfo { Path = path720 } },
                },
            },
            TotalRecordCount = 1,
        };

        var context = CreateResultContext(httpContext, queryResult);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        Assert.Single(queryResult.Items);
    }

    [Fact]
    public async Task ResultFilter_IntrosPath_IsExcluded()
    {
        var userId = Guid.NewGuid();

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Users/" + userId + "/Items/00000000-0000-0000-0000-000000000001/Intros",
            userId: userId);

        var playbackInfo = new PlaybackInfoResponse
        {
            MediaSources = new[]
            {
                new MediaSourceInfo { Path = "/media/Movie - 1080p.mkv" },
            },
        };

        var context = CreateResultContext(httpContext, playbackInfo);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        // Intros endpoint should be excluded from filtering
        Assert.Single(playbackInfo.MediaSources);
    }

    // --- OnResourceExecutionAsync tests ---

    [Fact]
    public async Task ResourceFilter_NonPlaybackInfo_PassesThrough()
    {
        var httpContext = CreateHttpContext("/api/System/Info", method: "POST", userId: Guid.NewGuid());
        var context = CreateResourceContext(httpContext);
        var executed = false;

        Task<ResourceExecutedContext> Next()
        {
            executed = true;
            return Task.FromResult(new ResourceExecutedContext(
                context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);
        Assert.True(executed);
    }

    [Fact]
    public async Task ResourceFilter_PlaybackInfoGet_PassesThrough()
    {
        var httpContext = CreateHttpContext(
            "/Items/00000000-0000-0000-0000-000000000001/PlaybackInfo",
            method: "GET",
            userId: Guid.NewGuid());
        var context = CreateResourceContext(httpContext);
        var executed = false;

        Task<ResourceExecutedContext> Next()
        {
            executed = true;
            return Task.FromResult(new ResourceExecutedContext(
                context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);
        Assert.True(executed);
        // GET requests should not trigger body modification
        Assert.False(httpContext.Items.ContainsKey("QG_ForcedTranscode"));
    }

    [Fact]
    public async Task ResourceFilter_PlaybackInfoPost_NoUser_PassesThrough()
    {
        var httpContext = CreateHttpContext(
            "/Items/00000000-0000-0000-0000-000000000001/PlaybackInfo",
            method: "POST");

        // Provide a body so the filter can read it
        var body = JsonSerializer.SerializeToUtf8Bytes(new { });
        httpContext.Request.Body = new MemoryStream(body);
        httpContext.Request.ContentLength = body.Length;
        httpContext.Request.ContentType = "application/json";

        var context = CreateResourceContext(httpContext);
        var executed = false;

        Task<ResourceExecutedContext> Next()
        {
            executed = true;
            return Task.FromResult(new ResourceExecutedContext(
                context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);
        Assert.True(executed);
        Assert.False(httpContext.Items.ContainsKey("QG_ForcedTranscode"));
    }

    [Fact]
    public async Task ResultFilter_IEnumerableBaseItemDto_FiltersBlockedItems()
    {
        var userId = Guid.NewGuid();
        var path720 = CreateTempFile("Movie - 720p.mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Users/" + userId + "/Items/Latest",
            userId: userId);

        var items = new List<BaseItemDto>
        {
            new BaseItemDto
            {
                Name = "Good Movie",
                MediaSources = new[] { new MediaSourceInfo { Path = path720 } },
            },
            new BaseItemDto
            {
                Name = "Blocked Movie",
                MediaSources = new[] { new MediaSourceInfo { Path = CreateTempFile("Movie - 4K.mkv") } },
            },
        };

        // Cast to IEnumerable to simulate lazy enumerable from /Items/Latest
        var enumerable = items.Select(x => x);

        var context = CreateResultContext(httpContext, enumerable);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        var result = ((ObjectResult)context.Result).Value as List<BaseItemDto>;
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Good Movie", result[0].Name);
    }

    [Fact]
    public async Task ResultFilter_QueryResult_FallbackTranscode_DoesNotHideItem()
    {
        var userId = Guid.NewGuid();
        var pathRemux = CreateTempFile("Movie [BDRemux].mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true),
            },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Users/" + userId + "/Items",
            userId: userId);

        var item = new BaseItemDto
        {
            Name = "Remux Movie",
            MediaSources = new[]
            {
                new MediaSourceInfo
                {
                    Path = pathRemux,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                },
            },
        };

        var queryResult = new QueryResult<BaseItemDto>
        {
            Items = new[] { item },
            TotalRecordCount = 1,
        };

        var context = CreateResultContext(httpContext, queryResult);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        // Item should not be hidden because fallback transcode is enabled
        Assert.Single(queryResult.Items);
        Assert.False(queryResult.Items[0].MediaSources[0].SupportsDirectPlay);
    }

    [Fact]
    public async Task ResultFilter_QueryResult_LibraryLookup_NullItem_DoesNotHide()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Users/" + userId + "/Items",
            userId: userId);

        var item = new BaseItemDto
        {
            Id = itemId,
            Name = "Unknown Item",
            MediaSources = null,
        };

        // Library returns null for item
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns((MediaBrowser.Controller.Entities.BaseItem?)null);

        var queryResult = new QueryResult<BaseItemDto>
        {
            Items = new[] { item },
            TotalRecordCount = 1,
        };

        var context = CreateResultContext(httpContext, queryResult);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        // Item should not be hidden when library lookup returns null
        Assert.Single(queryResult.Items);
    }

    [Fact]
    public async Task ResultFilter_QueryResult_LibraryLookup_FallbackTranscode_NotHidden()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var pathRemux = CreateTempFile("Movie [BDRemux].mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true),
            },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Users/" + userId + "/Items",
            userId: userId);

        var item = new BaseItemDto
        {
            Id = itemId,
            Name = "Remux from Library",
            MediaSources = null, // Forces library lookup
        };

        var mockBaseItem = new Mock<MediaBrowser.Controller.Entities.BaseItem>();
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(mockBaseItem.Object);
        _mediaSourceManagerMock.Setup(m => m.GetStaticMediaSources(It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(), It.IsAny<bool>(), It.IsAny<Jellyfin.Database.Implementations.Entities.User>()))
            .Returns(new List<MediaSourceInfo>
            {
                new MediaSourceInfo { Path = pathRemux },
            });

        var queryResult = new QueryResult<BaseItemDto>
        {
            Items = new[] { item },
            TotalRecordCount = 1,
        };

        var context = CreateResultContext(httpContext, queryResult);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        // Item should not be hidden because fallback transcode is enabled
        Assert.Single(queryResult.Items);
    }

    [Fact]
    public async Task ResultFilter_QueryResult_LibraryLookup_NoSources_NotHidden()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Users/" + userId + "/Items",
            userId: userId);

        var item = new BaseItemDto
        {
            Id = itemId,
            Name = "No Sources Item",
            MediaSources = null,
        };

        var mockBaseItem = new Mock<MediaBrowser.Controller.Entities.BaseItem>();
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(mockBaseItem.Object);
        _mediaSourceManagerMock.Setup(m => m.GetStaticMediaSources(It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(), It.IsAny<bool>(), It.IsAny<Jellyfin.Database.Implementations.Entities.User>()))
            .Returns(new List<MediaSourceInfo>());

        var queryResult = new QueryResult<BaseItemDto>
        {
            Items = new[] { item },
            TotalRecordCount = 1,
        };

        var context = CreateResultContext(httpContext, queryResult);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        // Empty sources → not hidden
        Assert.Single(queryResult.Items);
    }

    [Fact]
    public async Task ResultFilter_QueryResult_LibraryLookupThrows_NotHidden()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Users/" + userId + "/Items",
            userId: userId);

        var item = new BaseItemDto
        {
            Id = itemId,
            Name = "Error Item",
            MediaSources = null,
        };

        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Throws(new InvalidOperationException("test error"));

        var queryResult = new QueryResult<BaseItemDto>
        {
            Items = new[] { item },
            TotalRecordCount = 1,
        };

        var context = CreateResultContext(httpContext, queryResult);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        // Error during lookup → not hidden (fail-open for visibility)
        Assert.Single(queryResult.Items);
    }

    [Fact]
    public async Task ResultFilter_PlaybackInfo_EmptyMediaSources_NoChange()
    {
        var userId = Guid.NewGuid();

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Items/00000000-0000-0000-0000-000000000001/PlaybackInfo",
            userId: userId);

        var playbackInfo = new PlaybackInfoResponse
        {
            MediaSources = Array.Empty<MediaSourceInfo>(),
        };

        var context = CreateResultContext(httpContext, playbackInfo);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        // Empty sources, no filtering applied
        Assert.Empty(playbackInfo.MediaSources);
    }

    [Fact]
    public async Task ResultFilter_JellyfinUserIdClaim_IsRecognized()
    {
        var userId = Guid.NewGuid();
        var path720 = CreateTempFile("Movie - 720p.mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { MakePolicy(allowedPatterns: new List<string> { "- 720p" }) },
            DefaultPolicyId = "p1",
        });

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/Items/00000000-0000-0000-0000-000000000001/PlaybackInfo";
        httpContext.Request.Method = "GET";

        // Use the Jellyfin-specific claim
        var claims = new List<Claim>
        {
            new Claim("Jellyfin-UserId", userId.ToString()),
        };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        var playbackInfo = new PlaybackInfoResponse
        {
            MediaSources = new[]
            {
                new MediaSourceInfo { Path = path720 },
                new MediaSourceInfo { Path = CreateTempFile("Movie - 4K.mkv") },
            },
        };

        var context = CreateResultContext(httpContext, playbackInfo);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        Assert.Single(playbackInfo.MediaSources);
        Assert.Equal(path720, playbackInfo.MediaSources[0].Path);
    }

    [Fact]
    public async Task ResultFilter_PolicyIntroPath_IsRecognized()
    {
        var userId = Guid.NewGuid();
        var introPath = "/media/intros/policy-intro.mp4";

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, introVideoPath: introPath),
            },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Items/00000000-0000-0000-0000-000000000001/PlaybackInfo",
            userId: userId);

        var playbackInfo = new PlaybackInfoResponse
        {
            MediaSources = new[]
            {
                new MediaSourceInfo { Path = introPath },
            },
        };

        var context = CreateResultContext(httpContext, playbackInfo);
        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);

        // Policy intro path should skip filtering
        Assert.Single(playbackInfo.MediaSources);
    }

    // --- TryForceTranscodeBodyAsync (Phase 1 resource filter) tests ---

    [Fact]
    public async Task ResourceFilter_PlaybackInfoPost_NoPolicy_DoesNotModifyBody()
    {
        var userId = Guid.NewGuid();
        SetConfig(new PluginConfiguration()); // No policies

        var itemId = Guid.NewGuid();
        var httpContext = CreateHttpContext(
            $"/Items/{itemId}/PlaybackInfo",
            method: "POST",
            userId: userId);

        var bodyJson = JsonSerializer.Serialize(new { DeviceProfile = new { DirectPlayProfiles = new[] { new { Type = "Video" } } } });
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.ContentType = "application/json";

        var context = CreateResourceContext(httpContext);
        Task<ResourceExecutedContext> Next()
        {
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);

        Assert.False(httpContext.Items.ContainsKey("QG_ForcedTranscode"));
    }

    [Fact]
    public async Task ResourceFilter_PlaybackInfoPost_DenyAllPolicy_DoesNotForceFallback()
    {
        var userId = Guid.NewGuid();
        // DenyAll policy — user override points to nonexistent policy
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>(),
            DefaultPolicyId = "nonexistent",
        });

        var itemId = Guid.NewGuid();
        var httpContext = CreateHttpContext(
            $"/Items/{itemId}/PlaybackInfo",
            method: "POST",
            userId: userId);

        var bodyJson = "{}";
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.ContentType = "application/json";

        var context = CreateResourceContext(httpContext);
        Task<ResourceExecutedContext> Next()
        {
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);

        // DenyAll policy should NOT trigger fallback
        Assert.False(httpContext.Items.ContainsKey("QG_ForcedTranscode"));
    }

    [Fact]
    public async Task ResourceFilter_PlaybackInfoPost_FallbackEnabled_AllBlocked_ForcesTranscode()
    {
        var userId = Guid.NewGuid();
        var pathRemux = CreateTempFile("Movie [BDRemux].mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true),
            },
            DefaultPolicyId = "p1",
        });

        var itemId = Guid.NewGuid();
        var mockItem = new Mock<MediaBrowser.Controller.Entities.BaseItem>();
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(mockItem.Object);
        _mediaSourceManagerMock.Setup(m => m.GetStaticMediaSources(
            It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(),
            It.IsAny<bool>(),
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>()))
            .Returns(new List<MediaSourceInfo>
            {
                new MediaSourceInfo { Path = pathRemux },
            });

        var httpContext = CreateHttpContext(
            $"/Items/{itemId}/PlaybackInfo",
            method: "POST",
            userId: userId);

        var bodyJson = JsonSerializer.Serialize(new
        {
            DeviceProfile = new
            {
                DirectPlayProfiles = new[] { new { Type = "Video" } },
                TranscodingProfiles = new[] { new { Container = "ts", Type = "Video" } },
            }
        });
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.ContentType = "application/json";

        var context = CreateResourceContext(httpContext);
        Task<ResourceExecutedContext> Next()
        {
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);

        Assert.True(httpContext.Items.ContainsKey("QG_ForcedTranscode"));
    }

    [Fact]
    public async Task ResourceFilter_PlaybackInfoPost_FallbackEnabled_SomeAllowed_DoesNotForce()
    {
        var userId = Guid.NewGuid();
        var path720 = CreateTempFile("Movie - 720p.mkv");
        var pathRemux = CreateTempFile("Movie [BDRemux].mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true),
            },
            DefaultPolicyId = "p1",
        });

        var itemId = Guid.NewGuid();
        var mockItem = new Mock<MediaBrowser.Controller.Entities.BaseItem>();
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(mockItem.Object);
        _mediaSourceManagerMock.Setup(m => m.GetStaticMediaSources(
            It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(),
            It.IsAny<bool>(),
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>()))
            .Returns(new List<MediaSourceInfo>
            {
                new MediaSourceInfo { Path = path720 },
                new MediaSourceInfo { Path = pathRemux },
            });

        var httpContext = CreateHttpContext(
            $"/Items/{itemId}/PlaybackInfo",
            method: "POST",
            userId: userId);

        var bodyJson = "{}";
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.ContentType = "application/json";

        var context = CreateResourceContext(httpContext);
        Task<ResourceExecutedContext> Next()
        {
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);

        // Some sources pass policy — no forced transcode needed
        Assert.False(httpContext.Items.ContainsKey("QG_ForcedTranscode"));
    }

    [Fact]
    public async Task ResourceFilter_PlaybackInfoPost_NoItemId_DoesNotForce()
    {
        var userId = Guid.NewGuid();
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true),
            },
            DefaultPolicyId = "p1",
        });

        // Path without valid item ID
        var httpContext = CreateHttpContext(
            "/PlaybackInfo",
            method: "POST",
            userId: userId);

        var bodyJson = "{}";
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.ContentType = "application/json";

        var context = CreateResourceContext(httpContext);
        Task<ResourceExecutedContext> Next()
        {
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);

        Assert.False(httpContext.Items.ContainsKey("QG_ForcedTranscode"));
    }

    [Fact]
    public async Task ResourceFilter_PlaybackInfoPost_ItemNotInLibrary_DoesNotForce()
    {
        var userId = Guid.NewGuid();
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true),
            },
            DefaultPolicyId = "p1",
        });

        var itemId = Guid.NewGuid();
        _libraryManagerMock.Setup(l => l.GetItemById(itemId))
            .Returns((MediaBrowser.Controller.Entities.BaseItem?)null);

        var httpContext = CreateHttpContext(
            $"/Items/{itemId}/PlaybackInfo",
            method: "POST",
            userId: userId);

        var bodyJson = "{}";
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.ContentType = "application/json";

        var context = CreateResourceContext(httpContext);
        Task<ResourceExecutedContext> Next()
        {
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);

        Assert.False(httpContext.Items.ContainsKey("QG_ForcedTranscode"));
    }

    [Fact]
    public async Task ResourceFilter_PlaybackInfoPost_EmptyBody_ForcesTranscodeWithInjectedProfile()
    {
        var userId = Guid.NewGuid();
        var pathRemux = CreateTempFile("Movie [BDRemux].mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true),
            },
            DefaultPolicyId = "p1",
        });

        var itemId = Guid.NewGuid();
        var mockItem = new Mock<MediaBrowser.Controller.Entities.BaseItem>();
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(mockItem.Object);
        _mediaSourceManagerMock.Setup(m => m.GetStaticMediaSources(
            It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(),
            It.IsAny<bool>(),
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>()))
            .Returns(new List<MediaSourceInfo>
            {
                new MediaSourceInfo { Path = pathRemux },
            });

        var httpContext = CreateHttpContext(
            $"/Items/{itemId}/PlaybackInfo",
            method: "POST",
            userId: userId);

        // Empty body — DeviceProfile will be injected
        var bodyBytes = Encoding.UTF8.GetBytes("");
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.ContentType = "application/json";

        var context = CreateResourceContext(httpContext);
        Task<ResourceExecutedContext> Next()
        {
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);

        Assert.True(httpContext.Items.ContainsKey("QG_ForcedTranscode"));
    }

    [Fact]
    public async Task ResourceFilter_PlaybackInfoPost_FallbackWithMaxHeight_CapsResolution()
    {
        var userId = Guid.NewGuid();
        var pathRemux = CreateTempFile("Movie [BDRemux]-cap.mkv");

        var policy = new QualityPolicy
        {
            Id = "p1",
            Name = "Capped Policy",
            Enabled = true,
            AllowedFilenamePatterns = new List<string> { "- 720p" },
            BlockedFilenamePatterns = new List<string>(),
            FallbackTranscode = true,
            FallbackMaxHeight = 720,
        };

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { policy },
            DefaultPolicyId = "p1",
        });

        var itemId = Guid.NewGuid();
        var mockItem = new Mock<MediaBrowser.Controller.Entities.BaseItem>();
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(mockItem.Object);
        _mediaSourceManagerMock.Setup(m => m.GetStaticMediaSources(
            It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(),
            It.IsAny<bool>(),
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>()))
            .Returns(new List<MediaSourceInfo>
            {
                new MediaSourceInfo { Path = pathRemux },
            });

        var httpContext = CreateHttpContext(
            $"/Items/{itemId}/PlaybackInfo",
            method: "POST",
            userId: userId);

        var bodyJson = JsonSerializer.Serialize(new
        {
            DeviceProfile = new
            {
                DirectPlayProfiles = new[] { new { Type = "Video" } },
                TranscodingProfiles = new[] { new { Container = "ts", Type = "Video" } },
                CodecProfiles = Array.Empty<object>(),
            }
        });
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.ContentType = "application/json";

        var context = CreateResourceContext(httpContext);
        Task<ResourceExecutedContext> Next()
        {
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);

        Assert.True(httpContext.Items.ContainsKey("QG_ForcedTranscode"));

        // Verify body was modified — read back the request body
        httpContext.Request.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Request.Body);
        var modifiedBody = await reader.ReadToEndAsync();
        Assert.Contains("MaxStreamingBitrate", modifiedBody);
        Assert.Contains("CodecProfiles", modifiedBody);
    }

    [Fact]
    public async Task ResourceFilter_PlaybackInfoPost_FallbackWithCustomBitrate_UsesCustom()
    {
        var userId = Guid.NewGuid();
        var pathRemux = CreateTempFile("Movie [BDRemux]-bitrate.mkv");

        var policy = new QualityPolicy
        {
            Id = "p1",
            Name = "Custom Bitrate Policy",
            Enabled = true,
            AllowedFilenamePatterns = new List<string> { "- 720p" },
            BlockedFilenamePatterns = new List<string>(),
            FallbackTranscode = true,
            FallbackMaxHeight = 720,
            FallbackMaxBitrateKbps = 5000,
        };

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy> { policy },
            DefaultPolicyId = "p1",
        });

        var itemId = Guid.NewGuid();
        var mockItem = new Mock<MediaBrowser.Controller.Entities.BaseItem>();
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(mockItem.Object);
        _mediaSourceManagerMock.Setup(m => m.GetStaticMediaSources(
            It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(),
            It.IsAny<bool>(),
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>()))
            .Returns(new List<MediaSourceInfo>
            {
                new MediaSourceInfo { Path = pathRemux },
            });

        var httpContext = CreateHttpContext(
            $"/Items/{itemId}/PlaybackInfo",
            method: "POST",
            userId: userId);

        var bodyJson = JsonSerializer.Serialize(new
        {
            DeviceProfile = new
            {
                DirectPlayProfiles = new[] { new { Type = "Video" } },
                TranscodingProfiles = new[] { new { Container = "ts", Type = "Video" } },
            }
        });
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.ContentType = "application/json";

        var context = CreateResourceContext(httpContext);
        Task<ResourceExecutedContext> Next()
        {
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);

        Assert.True(httpContext.Items.ContainsKey("QG_ForcedTranscode"));

        httpContext.Request.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Request.Body);
        var modifiedBody = await reader.ReadToEndAsync();
        // Custom bitrate of 5000 kbps = 5000000 bps
        Assert.Contains("5000000", modifiedBody);
    }

    [Fact]
    public async Task ResourceFilter_PlaybackInfoPost_NoSources_DoesNotForce()
    {
        var userId = Guid.NewGuid();
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true),
            },
            DefaultPolicyId = "p1",
        });

        var itemId = Guid.NewGuid();
        var mockItem = new Mock<MediaBrowser.Controller.Entities.BaseItem>();
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(mockItem.Object);
        _mediaSourceManagerMock.Setup(m => m.GetStaticMediaSources(
            It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(),
            It.IsAny<bool>(),
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>()))
            .Returns(new List<MediaSourceInfo>());

        var httpContext = CreateHttpContext(
            $"/Items/{itemId}/PlaybackInfo",
            method: "POST",
            userId: userId);

        var bodyBytes = Encoding.UTF8.GetBytes("{}");
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.ContentType = "application/json";

        var context = CreateResourceContext(httpContext);
        Task<ResourceExecutedContext> Next()
        {
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);

        Assert.False(httpContext.Items.ContainsKey("QG_ForcedTranscode"));
    }

    [Fact]
    public async Task ResourceFilter_PlaybackInfoPost_FallbackDisabled_DoesNotForce()
    {
        var userId = Guid.NewGuid();
        var pathRemux = CreateTempFile("Movie [BDRemux]-nofb.mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: false),
            },
            DefaultPolicyId = "p1",
        });

        var itemId = Guid.NewGuid();
        var mockItem = new Mock<MediaBrowser.Controller.Entities.BaseItem>();
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(mockItem.Object);
        _mediaSourceManagerMock.Setup(m => m.GetStaticMediaSources(
            It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(),
            It.IsAny<bool>(),
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>()))
            .Returns(new List<MediaSourceInfo>
            {
                new MediaSourceInfo { Path = pathRemux },
            });

        var httpContext = CreateHttpContext(
            $"/Items/{itemId}/PlaybackInfo",
            method: "POST",
            userId: userId);

        var bodyBytes = Encoding.UTF8.GetBytes("{}");
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.ContentType = "application/json";

        var context = CreateResourceContext(httpContext);
        Task<ResourceExecutedContext> Next()
        {
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);

        Assert.False(httpContext.Items.ContainsKey("QG_ForcedTranscode"));
    }

    [Fact]
    public async Task ResourceFilter_PlaybackInfoPost_NoDeviceProfile_InjectsMinimalProfile()
    {
        var userId = Guid.NewGuid();
        var pathRemux = CreateTempFile("Movie [BDRemux]-noprofile.mkv");

        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true),
            },
            DefaultPolicyId = "p1",
        });

        var itemId = Guid.NewGuid();
        var mockItem = new Mock<MediaBrowser.Controller.Entities.BaseItem>();
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(mockItem.Object);
        _mediaSourceManagerMock.Setup(m => m.GetStaticMediaSources(
            It.IsAny<MediaBrowser.Controller.Entities.BaseItem>(),
            It.IsAny<bool>(),
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>()))
            .Returns(new List<MediaSourceInfo>
            {
                new MediaSourceInfo { Path = pathRemux },
            });

        var httpContext = CreateHttpContext(
            $"/Items/{itemId}/PlaybackInfo",
            method: "POST",
            userId: userId);

        // Body without DeviceProfile
        var bodyJson = JsonSerializer.Serialize(new { SomeOtherProp = true });
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.ContentType = "application/json";

        var context = CreateResourceContext(httpContext);
        Task<ResourceExecutedContext> Next()
        {
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);

        Assert.True(httpContext.Items.ContainsKey("QG_ForcedTranscode"));

        httpContext.Request.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Request.Body);
        var modifiedBody = await reader.ReadToEndAsync();
        // Minimal DeviceProfile should be injected
        Assert.Contains("DeviceProfile", modifiedBody);
        Assert.Contains("TranscodingProfiles", modifiedBody);
        Assert.Contains("DirectPlayProfiles", modifiedBody);
    }

    [Fact]
    public async Task ResourceFilter_PlaybackInfoPost_InvalidItemGuid_DoesNotForce()
    {
        var userId = Guid.NewGuid();
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                MakePolicy(allowedPatterns: new List<string> { "- 720p" }, fallbackTranscode: true),
            },
            DefaultPolicyId = "p1",
        });

        var httpContext = CreateHttpContext(
            "/Items/not-a-guid/PlaybackInfo",
            method: "POST",
            userId: userId);

        var bodyBytes = Encoding.UTF8.GetBytes("{}");
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.ContentType = "application/json";

        var context = CreateResourceContext(httpContext);
        Task<ResourceExecutedContext> Next()
        {
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);

        Assert.False(httpContext.Items.ContainsKey("QG_ForcedTranscode"));
    }
}
