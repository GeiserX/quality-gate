using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Filters;
using Jellyfin.Plugin.QualityGate.Services;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.QualityGate.Tests;

[Collection(PluginInstanceCollection.Name)]
public class ResolutionCapFilterTests : IDisposable
{
    private const int Cap = 720;

    private static readonly Guid ItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly Plugin _plugin;
    private readonly string _tempDir;
    private readonly Mock<ILogger<ResolutionCapFilter>> _loggerMock;
    private readonly Mock<IMediaSourceManager> _mediaSourceManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly ResolutionCapFilter _filter;

    public ResolutionCapFilterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qg-cap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.SetReturnsDefault<string>(_tempDir);
        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(new PluginConfiguration());
        _plugin = new Plugin(appPaths.Object, xmlSerializer.Object);

        _loggerMock = new Mock<ILogger<ResolutionCapFilter>>();
        _mediaSourceManagerMock = new Mock<IMediaSourceManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _filter = new ResolutionCapFilter(
            _loggerMock.Object,
            _mediaSourceManagerMock.Object,
            _libraryManagerMock.Object,
            _userManagerMock.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }

        GC.SuppressFinalize(this);
    }

    // --- helpers ---

    private void SetConfig(PluginConfiguration config)
    {
        _plugin.Configuration.Policies = config.Policies;
        _plugin.Configuration.UserPolicies = config.UserPolicies;
        _plugin.Configuration.DefaultPolicyId = config.DefaultPolicyId;
        _plugin.Configuration.DefaultIntroVideoPath = config.DefaultIntroVideoPath;
        _plugin.Configuration.ApiKeyPolicyId = config.ApiKeyPolicyId;
    }

    /// <summary>Applies a policy capped at 720p to every user without an override.</summary>
    private void UseCappedPolicy(int maxHeight = Cap)
    {
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                new QualityPolicy { Id = "p1", Name = "720p tier", Enabled = true, MaxHeight = maxHeight },
            },
            DefaultPolicyId = "p1",
        });
    }

    /// <summary>Applies a policy with no height cap — the shape every existing config has.</summary>
    private void UseUncappedPolicy()
    {
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                new QualityPolicy { Id = "p1", Name = "No cap", Enabled = true, MaxHeight = 0 },
            },
            DefaultPolicyId = "p1",
        });
    }

    /// <summary>No policy at all — a donor.</summary>
    private void UseFullAccess()
    {
        SetConfig(new PluginConfiguration());
    }

    private static List<MediaStream> BuildStreams(int? height)
    {
        return new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Audio },

            // A null height is probed as video with no resolution recorded — the live
            // library's null-height case.
            new MediaStream { Type = MediaStreamType.Video, Height = height },
        };
    }

    /// <summary>Reports the same height for every item.</summary>
    private void SetItemHeight(int? height)
    {
        _mediaSourceManagerMock.Setup(m => m.GetMediaStreams(It.IsAny<Guid>())).Returns(BuildStreams(height));
    }

    /// <summary>Reports a height for one specific item, overriding any catch-all setup.</summary>
    private void SetItemHeight(Guid itemId, int? height)
    {
        _mediaSourceManagerMock.Setup(m => m.GetMediaStreams(itemId)).Returns(BuildStreams(height));
    }

    private static HttpContext CreateHttpContext(
        string path,
        string method = "GET",
        Guid? userId = null,
        Dictionary<string, string>? queryParams = null,
        bool useJellyfinClaim = true)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        httpContext.Request.Method = method;

        if (userId.HasValue)
        {
            var claim = useJellyfinClaim
                ? new Claim("Jellyfin-UserId", userId.Value.ToString("N"))
                : new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString());
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { claim }, "test"));
        }

        if (queryParams != null)
        {
            httpContext.Request.Query = new QueryCollection(queryParams.ToDictionary(
                kvp => kvp.Key,
                kvp => new Microsoft.Extensions.Primitives.StringValues(kvp.Value)));
        }

        return httpContext;
    }

    private static ResourceExecutingContext CreateResourceContext(HttpContext httpContext)
    {
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ResourceExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new List<IValueProviderFactory>());
    }

    private static ResultExecutingContext CreateResultContext(HttpContext httpContext, object? resultValue)
    {
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ResultExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new ObjectResult(resultValue),
            new object());
    }

    /// <summary>Runs the resource phase and reports whether the request was let through.</summary>
    private async Task<(bool Allowed, ResourceExecutingContext Context)> RunResourceAsync(HttpContext httpContext)
    {
        var context = CreateResourceContext(httpContext);
        var reachedAction = false;

        Task<ResourceExecutedContext> Next()
        {
            reachedAction = true;
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        }

        await _filter.OnResourceExecutionAsync(context, Next);
        return (reachedAction, context);
    }

    private async Task RunResultAsync(HttpContext httpContext, object? resultValue)
    {
        var context = CreateResultContext(httpContext, resultValue);

        Task<ResultExecutedContext> Next()
        {
            return Task.FromResult(new ResultExecutedContext(
                context, new List<IFilterMetadata>(), new ObjectResult(null), new object()));
        }

        await _filter.OnResultExecutionAsync(context, Next);
    }

    private static void AssertRefused(bool allowed, ResourceExecutingContext context)
    {
        Assert.False(allowed);
        var result = Assert.IsType<StatusCodeResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    private static void AssertAllowed(bool allowed, ResourceExecutingContext context)
    {
        Assert.True(allowed);
        Assert.Null(context.Result);
    }

    private void AssertLoggedAtLeastOnce(LogLevel level)
    {
        _loggerMock.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
            Times.AtLeastOnce);
    }

    private static string StreamPath(Guid itemId = default)
    {
        return $"/Videos/{(itemId == default ? ItemId : itemId)}/stream";
    }

    // --- the four required cases ---

    [Fact]
    public async Task RestrictedUser_OverCapItem_StaticRequest_IsRefused()
    {
        UseCappedPolicy();
        SetItemHeight(1080);

        var httpContext = CreateHttpContext(
            StreamPath(),
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string> { ["static"] = "true" });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertRefused(allowed, context);
        AssertLoggedAtLeastOnce(LogLevel.Warning);
    }

    [Fact]
    public async Task RestrictedUser_UnderCapItem_StaticRequest_IsAllowed()
    {
        UseCappedPolicy();
        SetItemHeight(720);

        var httpContext = CreateHttpContext(
            StreamPath(),
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string> { ["static"] = "true" });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertAllowed(allowed, context);
    }

    [Fact]
    public async Task UnrestrictedUser_OverCapItem_StaticRequest_IsAllowed()
    {
        UseFullAccess();
        SetItemHeight(1080);

        var httpContext = CreateHttpContext(
            StreamPath(),
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string> { ["static"] = "true" });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertAllowed(allowed, context);
        _mediaSourceManagerMock.Verify(m => m.GetMediaStreams(It.IsAny<Guid>()), Times.Never);
    }

    /// <summary>
    /// Chosen behaviour for an item whose height is unknown: ALLOW, and log a warning naming
    /// the item. A null height is the library answering "never probed", a data condition
    /// rather than a fault; refusing would take those items away from every restricted user
    /// at once. Negotiation still caps them, because the injected Height
    /// condition is marked required and an unknown value fails a required condition.
    /// A lookup that THROWS is a different thing entirely and is refused — see
    /// <see cref="WhenLookupThrows_DeliveryIsRefusedAndErrorLogged"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(DeliveryRoutes))]
    public async Task RestrictedUser_NullHeightItem_IsAllowedAndWarns(string path)
    {
        UseCappedPolicy();
        SetItemHeight(null);

        var httpContext = CreateHttpContext(
            path,
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string> { ["static"] = "true" });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertAllowed(allowed, context);
        AssertLoggedAtLeastOnce(LogLevel.Warning);
    }

    // --- transcode requests ---

    [Fact]
    public async Task RestrictedUser_OverCapItem_TranscodeHeldToCap_IsAllowed()
    {
        UseCappedPolicy();
        SetItemHeight(1080);

        var httpContext = CreateHttpContext(
            StreamPath(),
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string> { ["maxHeight"] = "720" });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertAllowed(allowed, context);
    }

    [Fact]
    public async Task RestrictedUser_OverCapItem_TranscodeWithNoCap_IsRefused()
    {
        UseCappedPolicy();
        SetItemHeight(1080);

        var httpContext = CreateHttpContext(StreamPath(), userId: Guid.NewGuid());

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertRefused(allowed, context);
    }

    [Fact]
    public async Task RestrictedUser_OverCapItem_TranscodeAboveCap_IsRefused()
    {
        UseCappedPolicy();
        SetItemHeight(2160);

        var httpContext = CreateHttpContext(
            StreamPath(),
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string> { ["maxHeight"] = "1080" });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertRefused(allowed, context);
    }

    // --- the params blob, which Jellyfin applies AFTER model binding ---

    [Fact]
    public async Task ParamsBlob_TurningOnStatic_IsRefused()
    {
        UseCappedPolicy();
        SetItemHeight(1080);

        // Index 3 of params sets Static inside the action, regardless of ?static.
        var httpContext = CreateHttpContext(
            StreamPath(),
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string>
            {
                ["maxHeight"] = "720",
                ["params"] = ";;;true",
            });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertRefused(allowed, context);
    }

    [Fact]
    public async Task ParamsBlob_RaisingMaxHeight_IsRefused()
    {
        UseCappedPolicy();
        SetItemHeight(1080);

        // Index 13 of params overwrites the bound MaxHeight inside the action.
        var httpContext = CreateHttpContext(
            StreamPath(),
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string>
            {
                ["maxHeight"] = "720",
                ["params"] = ";;;;;;;;;;;;;1080",
            });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertRefused(allowed, context);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task ParamsBlob_NonPositiveMaxHeight_ClearsTheNamedOne_AndIsRefused(string positional)
    {
        UseCappedPolicy();
        SetItemHeight(1080);

        // Jellyfin treats a non-positive MaxHeight as NO maximum, so pairing a named
        // maxHeight=720 with a positional 0 asks the server for an uncapped stream while the
        // request still looks negotiated. The positional value has to clear the named one.
        var httpContext = CreateHttpContext(
            StreamPath(),
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string>
            {
                ["maxHeight"] = "720",
                ["params"] = ";;;;;;;;;;;;;" + positional,
            });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertRefused(allowed, context);
    }

    [Fact]
    public async Task ParamsBlob_LoweringMaxHeightToCap_IsAllowed()
    {
        UseCappedPolicy();
        SetItemHeight(1080);

        var httpContext = CreateHttpContext(
            StreamPath(),
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string> { ["params"] = ";;;;;;;;;;;;;720" });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertAllowed(allowed, context);
    }

    // --- the other delivery routes ---

    /// <summary>Every route that hands back media bytes or a playlist.</summary>
    public static TheoryData<string> DeliveryRoutes => new TheoryData<string>
    {
        "/Videos/11111111-1111-1111-1111-111111111111/stream",
        "/Videos/11111111-1111-1111-1111-111111111111/stream.mkv",
        "/Videos/11111111-1111-1111-1111-111111111111/master.m3u8",
        "/Videos/11111111-1111-1111-1111-111111111111/main.m3u8",
        "/Videos/11111111-1111-1111-1111-111111111111/live.m3u8",
        "/Videos/11111111-1111-1111-1111-111111111111/hls1/main/0.mp4",
        "/Audio/11111111-1111-1111-1111-111111111111/stream",
        "/Items/11111111-1111-1111-1111-111111111111/File",
        "/Items/11111111-1111-1111-1111-111111111111/Download",
    };

    [Theory]
    [MemberData(nameof(DeliveryRoutes))]
    public async Task OverCapItem_IsRefusedOnEveryDeliveryRoute(string path)
    {
        UseCappedPolicy();
        SetItemHeight(1080);

        var httpContext = CreateHttpContext(
            path,
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string> { ["static"] = "true" });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertRefused(allowed, context);
    }

    [Fact]
    public async Task OriginalFileRoute_IgnoresTranscodeParameters()
    {
        UseCappedPolicy();
        SetItemHeight(1080);

        // /Items/{id}/File always returns the original, so maxHeight cannot make it acceptable.
        var httpContext = CreateHttpContext(
            $"/Items/{ItemId}/File",
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string> { ["maxHeight"] = "720" });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertRefused(allowed, context);
    }

    [Fact]
    public async Task BaseUrlPrefix_IsStillMatched()
    {
        UseCappedPolicy();
        SetItemHeight(1080);

        var httpContext = CreateHttpContext(
            $"/jellyfin/Videos/{ItemId}/stream",
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string> { ["static"] = "true" });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertRefused(allowed, context);
    }

    // --- which item a delivery request is measured against ---

    /// <summary>
    /// A media source id names a specific version, so it is measured — but so is the item in
    /// the route, because which one the action serves is settled after this filter has run.
    /// </summary>
    [Fact]
    public async Task MediaSourceId_IsMeasuredAlongsideTheRouteItem()
    {
        UseCappedPolicy();
        var versionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        SetItemHeight(1080);

        var httpContext = CreateHttpContext(
            StreamPath(),
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string>
            {
                ["static"] = "true",
                ["mediaSourceId"] = versionId.ToString(),
            });

        await RunResourceAsync(httpContext);

        _mediaSourceManagerMock.Verify(m => m.GetMediaStreams(versionId), Times.Once);
        _mediaSourceManagerMock.Verify(m => m.GetMediaStreams(ItemId), Times.Once);
    }

    /// <summary>
    /// The cap is enforced against the tallest candidate, so naming a small version alongside
    /// an over-cap route item does not buy a way past it.
    /// </summary>
    [Theory]
    [InlineData("mediaSourceId")]
    [InlineData("params")]
    public async Task StreamRoute_EnforcesAgainstTheTallestCandidate(string parameterName)
    {
        UseCappedPolicy();
        var versionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        SetItemHeight(1080);
        SetItemHeight(versionId, 480);

        var httpContext = CreateHttpContext(
            StreamPath(),
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string>
            {
                ["static"] = "true",
                [parameterName] = parameterName == "params"
                    ? ";;" + versionId
                    : versionId.ToString(),
            });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertRefused(allowed, context);
    }

    /// <summary>
    /// /Items/{itemId}/File and /Download take no media source parameter: they always return
    /// the file of the item in the route. The route id is therefore the only thing worth
    /// measuring, and a caller-supplied identifier must not be able to stand in for it.
    /// </summary>
    [Theory]
    [InlineData("/Items/11111111-1111-1111-1111-111111111111/File", "mediaSourceId")]
    [InlineData("/Items/11111111-1111-1111-1111-111111111111/File", "params")]
    [InlineData("/Items/11111111-1111-1111-1111-111111111111/Download", "mediaSourceId")]
    [InlineData("/Items/11111111-1111-1111-1111-111111111111/Download", "params")]
    public async Task OriginalFileRoute_MeasuresTheRouteItem_NotACallerSuppliedId(string path, string parameterName)
    {
        UseCappedPolicy();
        var lowResVersionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        SetItemHeight(ItemId, 1080);
        SetItemHeight(lowResVersionId, 480);

        var httpContext = CreateHttpContext(
            path,
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string>
            {
                [parameterName] = parameterName == "params"
                    ? ";;" + lowResVersionId
                    : lowResVersionId.ToString(),
            });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertRefused(allowed, context);
        _mediaSourceManagerMock.Verify(m => m.GetMediaStreams(ItemId), Times.Once);
        _mediaSourceManagerMock.Verify(m => m.GetMediaStreams(lowResVersionId), Times.Never);
    }

    // --- requests the filter must not touch ---

    [Fact]
    public async Task UnrelatedPath_IsNotEvaluated()
    {
        UseCappedPolicy();
        SetItemHeight(1080);

        var httpContext = CreateHttpContext("/System/Info", userId: Guid.NewGuid());

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertAllowed(allowed, context);
        _mediaSourceManagerMock.Verify(m => m.GetMediaStreams(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task PolicyWithoutHeightCap_ChangesNothing()
    {
        UseUncappedPolicy();
        SetItemHeight(2160);

        var httpContext = CreateHttpContext(
            StreamPath(),
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string> { ["static"] = "true" });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertAllowed(allowed, context);
        _mediaSourceManagerMock.Verify(m => m.GetMediaStreams(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task AnonymousRequest_IsNotEvaluated()
    {
        UseCappedPolicy();
        SetItemHeight(1080);

        var httpContext = CreateHttpContext(StreamPath());

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertAllowed(allowed, context);
    }

    [Fact]
    public async Task ApiKeyRequest_IsRefused_OnceAnApiKeyPolicyIsConfigured()
    {
        // The counterpart to AnonymousRequest_IsNotEvaluated above, which pins the default: with
        // nothing configured a request carrying no user is served uncapped. Here the operator has
        // opted in, so the same request must now be held to the cap.
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                new QualityPolicy { Id = "p1", Name = "720p tier", Enabled = true, MaxHeight = Cap },
            },
            ApiKeyPolicyId = "p1",
        });
        SetItemHeight(1080);

        var httpContext = CreateHttpContext(
            StreamPath(),
            queryParams: new Dictionary<string, string> { ["static"] = "true" });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertRefused(allowed, context);
    }

    [Fact]
    public async Task ApiKeyRequest_WithinTheCap_IsStillAllowed()
    {
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                new QualityPolicy { Id = "p1", Name = "720p tier", Enabled = true, MaxHeight = Cap },
            },
            ApiKeyPolicyId = "p1",
        });
        SetItemHeight(Cap);

        var httpContext = CreateHttpContext(
            StreamPath(),
            queryParams: new Dictionary<string, string> { ["static"] = "true" });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertAllowed(allowed, context);
    }

    [Fact]
    public async Task ApiKeyPolicyPointingAtNothing_LeavesTheRequestUncapped()
    {
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                new QualityPolicy { Id = "p1", Name = "720p tier", Enabled = true, MaxHeight = Cap },
            },
            ApiKeyPolicyId = "deleted-policy",
        });
        SetItemHeight(2160);

        var httpContext = CreateHttpContext(
            StreamPath(),
            queryParams: new Dictionary<string, string> { ["static"] = "true" });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertAllowed(allowed, context);
    }

    [Fact]
    public async Task NameIdentifierClaim_StillResolvesTheUser()
    {
        UseCappedPolicy();
        SetItemHeight(1080);

        var httpContext = CreateHttpContext(
            StreamPath(),
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string> { ["static"] = "true" },
            useJellyfinClaim: false);

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertRefused(allowed, context);
    }

    // --- the two halves of "the height is not a number": unknown, and unreadable ---

    /// <summary>
    /// A height lookup that THROWS is a defect in this plugin, not something the library said
    /// about the media. On a delivery route that means refusing: the request is for the bytes
    /// themselves, and allowing it hands a capped user exactly what the cap withholds.
    /// </summary>
    [Theory]
    [MemberData(nameof(DeliveryRoutes))]
    public async Task WhenLookupThrows_DeliveryIsRefusedAndErrorLogged(string path)
    {
        UseCappedPolicy();
        _mediaSourceManagerMock
            .Setup(m => m.GetMediaStreams(It.IsAny<Guid>()))
            .Throws(new InvalidOperationException("boom"));

        var httpContext = CreateHttpContext(
            path,
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string> { ["static"] = "true" });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertRefused(allowed, context);
        AssertLoggedAtLeastOnce(LogLevel.Error);
    }

    /// <summary>
    /// Negotiation keeps failing open. Nothing is being delivered yet, and a defect here must
    /// not take playback away from everyone.
    /// </summary>
    [Fact]
    public async Task WhenNegotiationThrows_RequestIsAllowedAndErrorLogged()
    {
        UseCappedPolicy();

        var httpContext = CreatePlaybackInfoPost(
            Guid.NewGuid(),
            "{\"DeviceProfile\":{\"DirectPlayProfiles\":[]}}");

        // A body the filter cannot read: reading it throws rather than returning JSON.
        httpContext.Request.Body.Dispose();

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertAllowed(allowed, context);
        AssertLoggedAtLeastOnce(LogLevel.Error);
    }

    [Fact]
    public async Task UnresolvableItemId_IsAllowed()
    {
        UseCappedPolicy();
        SetItemHeight(1080);

        var httpContext = CreateHttpContext(
            "/Videos/not-a-guid/stream",
            userId: Guid.NewGuid(),
            queryParams: new Dictionary<string, string> { ["static"] = "true" });

        var (allowed, context) = await RunResourceAsync(httpContext);

        AssertAllowed(allowed, context);
    }

    // --- negotiation: the PlaybackInfo response ---

    private static PlaybackInfoResponse ResponseWithHeights(params int?[] heights)
    {
        return new PlaybackInfoResponse
        {
            MediaSources = heights.Select((h, i) => new MediaSourceInfo
            {
                Id = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Path = $"/media/source-{i}.mkv",
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                MediaStreams = h.HasValue
                    ? new[] { new MediaStream { Type = MediaStreamType.Video, Height = h.Value } }
                    : Array.Empty<MediaStream>(),
            }).ToArray(),
        };
    }

    [Fact]
    public async Task PlaybackInfo_DropsOverCapSource_WhenAWithinCapSiblingExists()
    {
        UseCappedPolicy();
        var response = ResponseWithHeights(1080, 720);

        var httpContext = CreateHttpContext($"/Items/{ItemId}/PlaybackInfo", "POST", Guid.NewGuid());
        await RunResultAsync(httpContext, response);

        var kept = Assert.Single(response.MediaSources);
        Assert.Equal(720, kept.MediaStreams[0].Height);
    }

    [Fact]
    public async Task PlaybackInfo_ForAnApiKeyCaller_IsCapped_OnceAnApiKeyPolicyIsConfigured()
    {
        // The delivery half of this is covered above. This is the negotiation half: an API key
        // resolves to no user, so before the opt-in existed this response came back untouched and
        // the caller was offered the original.
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                new QualityPolicy { Id = "p1", Name = "720p tier", Enabled = true, MaxHeight = Cap },
            },
            ApiKeyPolicyId = "p1",
        });
        var response = ResponseWithHeights(1080, 720);

        // No userId: exactly what Jellyfin's API-key authentication produces.
        var httpContext = CreateHttpContext($"/Items/{ItemId}/PlaybackInfo", "POST");
        await RunResultAsync(httpContext, response);

        var kept = Assert.Single(response.MediaSources);
        Assert.Equal(Cap, kept.MediaStreams[0].Height);
    }

    [Fact]
    public async Task PlaybackInfo_ForAnApiKeyCaller_IsUntouched_WhenNoApiKeyPolicyIsConfigured()
    {
        // The default, and the reverse control for the test above: a capped policy exists and is
        // even the default for real users, but an API-key caller keeps every source.
        UseCappedPolicy();
        var response = ResponseWithHeights(1080, 720);

        var httpContext = CreateHttpContext($"/Items/{ItemId}/PlaybackInfo", "POST");
        await RunResultAsync(httpContext, response);

        Assert.Equal(2, response.MediaSources.Count);
    }

    [Fact]
    public async Task PlaybackInfo_WhenEverySourceIsOverCap_ForcesTranscode()
    {
        UseCappedPolicy();
        var response = ResponseWithHeights(1080, 2160);

        var httpContext = CreateHttpContext($"/Items/{ItemId}/PlaybackInfo", "POST", Guid.NewGuid());
        await RunResultAsync(httpContext, response);

        Assert.Equal(2, response.MediaSources.Count);
        Assert.All(response.MediaSources, s =>
        {
            Assert.False(s.SupportsDirectPlay);
            Assert.False(s.SupportsDirectStream);
        });
    }

    [Fact]
    public async Task PlaybackInfo_WhenEverySourceIsWithinCap_KeepsThemAllPlayableBestFirst()
    {
        UseCappedPolicy();
        var response = ResponseWithHeights(480, 720);

        var httpContext = CreateHttpContext($"/Items/{ItemId}/PlaybackInfo", "POST", Guid.NewGuid());
        await RunResultAsync(httpContext, response);

        // Nothing is over the cap, so nothing may be removed or made unplayable — but the
        // best of what is left still goes first, because clients play MediaSources[0].
        Assert.Equal(2, response.MediaSources.Count);
        Assert.All(response.MediaSources, s => Assert.True(s.SupportsDirectPlay));
        Assert.Equal(720, QualityGateService.GetVideoHeight(response.MediaSources[0]));
    }

    [Fact]
    public async Task PlaybackInfo_UnrestrictedUser_LosesNothingAndGetsTheBestSourceFirst()
    {
        UseFullAccess();
        var response = ResponseWithHeights(1080, 2160);

        var httpContext = CreateHttpContext($"/Items/{ItemId}/PlaybackInfo", "POST", Guid.NewGuid());
        await RunResultAsync(httpContext, response);

        // An uncapped user keeps every source. They are also the users who were being handed
        // the encoded sibling first, so ordering applies to them too.
        Assert.Equal(2, response.MediaSources.Count);
        Assert.All(response.MediaSources, s => Assert.True(s.SupportsDirectPlay));
        Assert.Equal(2160, QualityGateService.GetVideoHeight(response.MediaSources[0]));
    }

    [Fact]
    public async Task PlaybackInfo_NullHeightSource_IsNotDropped()
    {
        UseCappedPolicy();
        var response = ResponseWithHeights(null, 1080);

        var httpContext = CreateHttpContext($"/Items/{ItemId}/PlaybackInfo", "POST", Guid.NewGuid());
        await RunResultAsync(httpContext, response);

        var kept = Assert.Single(response.MediaSources);
        Assert.Empty(kept.MediaStreams);
    }

    [Fact]
    public async Task PlaybackInfo_NonPlaybackResult_IsIgnored()
    {
        UseCappedPolicy();

        var httpContext = CreateHttpContext($"/Items/{ItemId}/PlaybackInfo", "POST", Guid.NewGuid());
        await RunResultAsync(httpContext, "not a playback response");

        // Nothing to assert beyond "did not throw"; the filter must pass unknown shapes through.
        Assert.True(true);
    }

    // --- negotiation: the device profile in the request body ---

    private static HttpContext CreatePlaybackInfoPost(Guid userId, string body)
    {
        var httpContext = CreateHttpContext($"/Items/{ItemId}/PlaybackInfo", "POST", userId);
        var bytes = Encoding.UTF8.GetBytes(body);
        httpContext.Request.Body = new MemoryStream(bytes);
        httpContext.Request.ContentLength = bytes.Length;
        httpContext.Request.ContentType = "application/json";
        return httpContext;
    }

    private static string ReadBody(HttpContext httpContext)
    {
        httpContext.Request.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task PlaybackInfoPost_AddsRequiredHeightConditionToDeviceProfile()
    {
        UseCappedPolicy();
        var httpContext = CreatePlaybackInfoPost(
            Guid.NewGuid(),
            "{\"DeviceProfile\":{\"DirectPlayProfiles\":[],\"TranscodingProfiles\":[]}}");

        await RunResourceAsync(httpContext);

        var root = JsonNode.Parse(ReadBody(httpContext))!.AsObject();
        var codecProfiles = root["DeviceProfile"]!["CodecProfiles"]!.AsArray();
        var condition = codecProfiles[0]!["Conditions"]!.AsArray()[0]!.AsObject();

        Assert.Equal("Video", codecProfiles[0]!["Type"]!.GetValue<string>());
        Assert.Equal("LessThanEqual", condition["Condition"]!.GetValue<string>());
        Assert.Equal("Height", condition["Property"]!.GetValue<string>());
        Assert.Equal("720", condition["Value"]!.GetValue<string>());
        Assert.True(condition["IsRequired"]!.GetValue<bool>());

        // Width is deliberately left alone: a 2.39:1 film at 720p is wider than 16:9.
        Assert.DoesNotContain("\"Property\":\"Width\"", ReadBody(httpContext), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaybackInfoPost_KeepsExistingCodecProfiles()
    {
        UseCappedPolicy();
        var httpContext = CreatePlaybackInfoPost(
            Guid.NewGuid(),
            "{\"DeviceProfile\":{\"CodecProfiles\":[{\"Type\":\"VideoAudio\"}]}}");

        await RunResourceAsync(httpContext);

        var codecProfiles = JsonNode.Parse(ReadBody(httpContext))!
            .AsObject()["DeviceProfile"]!["CodecProfiles"]!.AsArray();

        Assert.Equal(2, codecProfiles.Count);
        Assert.Equal("VideoAudio", codecProfiles[0]!["Type"]!.GetValue<string>());
        Assert.Equal("Video", codecProfiles[1]!["Type"]!.GetValue<string>());
    }

    [Fact]
    public async Task PlaybackInfoPost_WithoutDeviceProfile_LeavesBodyAlone()
    {
        UseCappedPolicy();
        const string Body = "{\"MaxStreamingBitrate\":20000000}";
        var httpContext = CreatePlaybackInfoPost(Guid.NewGuid(), Body);

        await RunResourceAsync(httpContext);

        Assert.Equal(Body, ReadBody(httpContext));
    }

    [Fact]
    public async Task PlaybackInfoPost_UnrestrictedUser_LeavesBodyAlone()
    {
        UseFullAccess();
        const string Body = "{\"DeviceProfile\":{\"DirectPlayProfiles\":[]}}";
        var httpContext = CreatePlaybackInfoPost(Guid.NewGuid(), Body);

        await RunResourceAsync(httpContext);

        Assert.Equal(Body, ReadBody(httpContext));
    }

    [Fact]
    public async Task PlaybackInfoGet_LeavesBodyAlone()
    {
        UseCappedPolicy();
        const string Body = "{\"DeviceProfile\":{\"DirectPlayProfiles\":[]}}";
        var httpContext = CreatePlaybackInfoPost(Guid.NewGuid(), Body);
        httpContext.Request.Method = "GET";

        await RunResourceAsync(httpContext);

        Assert.Equal(Body, ReadBody(httpContext));
    }

    // --- negotiation: within-cap versions played as they are ---
    //
    // These run Jellyfin's own StreamBuilder over the device profile the filter rewrote, with the
    // ceiling MediaInfoController would hand it, so they show what the client is actually told.

    private const string OverCapId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string WithinCapId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly JsonSerializerOptions ProfileJson = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>A browser-like profile: plays h264/aac in mp4, transcodes to HLS, 4 Mbps ceiling.</summary>
    private const string BrowserBody = """
        {
          "MaxStreamingBitrate": 4000000,
          "DeviceProfile": {
            "MaxStreamingBitrate": 4000000,
            "DirectPlayProfiles": [ { "Container": "mp4", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac" } ],
            "TranscodingProfiles": [ { "Container": "ts", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac", "Protocol": "hls", "Context": "Streaming" } ],
            "CodecProfiles": [],
            "SubtitleProfiles": []
          }
        }
        """;

    private static MediaSourceInfo Version(
        string id,
        int? height,
        int bitrate,
        string container = "mp4",
        string videoCodec = "h264",
        int videoBitrate = 0,
        int audioBitrate = 192000)
    {
        return new MediaSourceInfo
        {
            Id = id,
            Path = $"/media/Show A (2001)/{id}.{container}",
            Protocol = MediaProtocol.File,
            Container = container,
            Bitrate = bitrate,
            RunTimeTicks = TimeSpan.FromMinutes(90).Ticks,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            DefaultAudioStreamIndex = 1,
            MediaStreams = new List<MediaStream>
            {
                new()
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = videoCodec,
                    Height = height,
                    Width = height.HasValue ? height.Value * 16 / 9 : null,
                    BitRate = videoBitrate > 0 ? videoBitrate : bitrate - audioBitrate,
                    BitDepth = 8,
                    IsDefault = true,
                },
                new()
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = "aac",
                    Channels = 2,
                    SampleRate = 48000,
                    BitRate = audioBitrate,
                    IsDefault = true,
                },
            },
        };
    }

    /// <summary>Applies a 720p policy with the option set as given.</summary>
    private void UseDirectWithinCapPolicy(bool keepDirect)
    {
        SetConfig(new PluginConfiguration
        {
            Policies = new List<QualityPolicy>
            {
                new QualityPolicy { Id = "p1", Name = "720p tier", Enabled = true, MaxHeight = Cap, KeepWithinCapVersionsDirect = keepDirect },
            },
            DefaultPolicyId = "p1",
        });
    }

    /// <summary>Makes the item carry exactly these versions.</summary>
    private void SetItemVersions(params MediaSourceInfo[] versions)
    {
        _libraryManagerMock.Setup(l => l.GetItemById(ItemId)).Returns(new Movie { Id = ItemId, Name = "Show A" });
        _mediaSourceManagerMock
            .Setup(m => m.GetStaticMediaSources(It.IsAny<BaseItem>(), false, It.IsAny<User?>()))
            .Returns(versions);
    }

    private async Task<HttpContext> NegotiateAsync(string body, Dictionary<string, string>? query = null)
    {
        var httpContext = CreatePlaybackInfoPost(Guid.NewGuid(), body);
        if (query != null)
        {
            // Through the query string, so the collection is parsed the way a server parses it
            // (keys case-insensitive), not built by hand.
            httpContext.Request.QueryString = QueryString.Create(query!);
        }

        var (allowed, _) = await RunResourceAsync(httpContext);
        Assert.True(allowed);
        return httpContext;
    }

    /// <summary>
    /// What the client would be told for one version: Jellyfin's StreamBuilder over the rewritten
    /// profile, with the ceiling MediaInfoController.GetPostedPlaybackInfo passes on (query value,
    /// else body value, else the profile's), and direct stream off as MediaInfoHelper sets it.
    /// </summary>
    private static StreamInfo Decide(HttpContext httpContext, MediaSourceInfo version)
    {
        var root = JsonNode.Parse(ReadBody(httpContext))!.AsObject();
        var profile = root["DeviceProfile"].Deserialize<DeviceProfile>(ProfileJson)!;

        int? ceiling = httpContext.Request.Query.TryGetValue("maxStreamingBitrate", out var q)
            ? int.Parse(q.ToString(), System.Globalization.CultureInfo.InvariantCulture)
            : root["MaxStreamingBitrate"]?.GetValue<int>() ?? profile.MaxStreamingBitrate;

        var transcoder = new Mock<ITranscoderSupport>();
        transcoder.Setup(t => t.CanEncodeToAudioCodec(It.IsAny<string>())).Returns(true);
        transcoder.Setup(t => t.CanEncodeToSubtitleCodec(It.IsAny<string>())).Returns(true);
        transcoder.Setup(t => t.CanExtractSubtitles(It.IsAny<string>())).Returns(true);

        // A fresh copy per decision: StreamBuilder writes to the source it is given.
        var copy = JsonSerializer.Deserialize<MediaSourceInfo>(JsonSerializer.SerializeToUtf8Bytes(version))!;
        var options = new MediaOptions
        {
            Profile = profile,
            MediaSources = new[] { copy },
            ItemId = ItemId,
            DeviceId = "test-device",
            MaxBitrate = ceiling,
            EnableDirectStream = false,
        };

        return new StreamBuilder(transcoder.Object, NullLogger.Instance).GetOptimalVideoStream(options)!;
    }

    private static long? BodyCeiling(HttpContext httpContext) =>
        JsonNode.Parse(ReadBody(httpContext))!["MaxStreamingBitrate"]?.GetValue<long>();

    private static long? ProfileCeiling(HttpContext httpContext) =>
        JsonNode.Parse(ReadBody(httpContext))!["DeviceProfile"]!["MaxStreamingBitrate"]?.GetValue<long>();

    [Fact]
    public async Task WithinCapVersion_HeldBackOnlyByBitrate_IsTranscoded_WhenTheOptionIsOff()
    {
        // The control for the next test: without the option a 4 Mbps client gets the 8 Mbps
        // 720p version as a transcode, for a bitrate reason and nothing else.
        UseDirectWithinCapPolicy(keepDirect: false);
        var withinCap = Version(WithinCapId, 720, 8_000_000);
        SetItemVersions(Version(OverCapId, 2160, 40_000_000, "mkv", "hevc"), withinCap);

        var httpContext = await NegotiateAsync(BrowserBody);
        var decision = Decide(httpContext, withinCap);

        Assert.Equal(PlayMethod.Transcode, decision.PlayMethod);
        Assert.Equal(TranscodeReason.ContainerBitrateExceedsLimit, decision.TranscodeReasons);
        Assert.Equal(4_000_000, BodyCeiling(httpContext));
        Assert.Equal(4_000_000, ProfileCeiling(httpContext));
    }

    [Fact]
    public async Task WithinCapVersion_HeldBackOnlyByBitrate_DirectPlays_WhenTheOptionIsOn()
    {
        UseDirectWithinCapPolicy(keepDirect: true);
        var withinCap = Version(WithinCapId, 720, 8_000_000);
        SetItemVersions(Version(OverCapId, 2160, 40_000_000, "mkv", "hevc"), withinCap);

        var httpContext = await NegotiateAsync(
            BrowserBody,
            new Dictionary<string, string> { ["maxStreamingBitrate"] = "4000000" });
        var decision = Decide(httpContext, withinCap);

        Assert.Equal(PlayMethod.DirectPlay, decision.PlayMethod);

        // Raised to exactly what the within-cap version needs, everywhere the client set it, and
        // never to the over-cap version's 40 Mbps.
        Assert.Equal("8000000", httpContext.Request.Query["maxStreamingBitrate"].ToString());
        Assert.Equal(8_000_000, BodyCeiling(httpContext));
        Assert.Equal(8_000_000, ProfileCeiling(httpContext));
    }

    [Fact]
    public async Task WithinCapVersion_WithACodecTheClientCannotPlay_StillTranscodes()
    {
        UseDirectWithinCapPolicy(keepDirect: true);
        var withinCap = Version(WithinCapId, 720, 8_000_000, "mkv", "hevc");
        SetItemVersions(withinCap);

        var httpContext = await NegotiateAsync(BrowserBody);
        var decision = Decide(httpContext, withinCap);

        Assert.Equal(PlayMethod.Transcode, decision.PlayMethod);
        Assert.NotEqual(0, (int)(decision.TranscodeReasons & (TranscodeReason.ContainerNotSupported | TranscodeReason.VideoCodecNotSupported)));
        Assert.Equal(0, (int)(decision.TranscodeReasons & TranscodeReason.ContainerBitrateExceedsLimit));
    }

    [Fact]
    public async Task ItemWithNoWithinCapVersion_KeepsTheClientCeiling()
    {
        UseDirectWithinCapPolicy(keepDirect: true);
        var overCap = Version(OverCapId, 1080, 12_000_000);
        SetItemVersions(overCap);

        var httpContext = await NegotiateAsync(
            BrowserBody,
            new Dictionary<string, string> { ["maxStreamingBitrate"] = "4000000" });
        var decision = Decide(httpContext, overCap);

        Assert.Equal("4000000", httpContext.Request.Query["maxStreamingBitrate"].ToString());
        Assert.Equal(4_000_000, BodyCeiling(httpContext));
        Assert.Equal(4_000_000, ProfileCeiling(httpContext));
        Assert.Equal(PlayMethod.Transcode, decision.PlayMethod);
        Assert.Equal(Cap, decision.MaxHeight);
    }

    [Fact]
    public async Task RequestNamingTheOverCapVersion_KeepsTheClientCeiling()
    {
        // The within-cap sibling exists, but this answer carries only the over-cap version, so
        // phase 2 cannot drop it and a raised ceiling would reach it.
        UseDirectWithinCapPolicy(keepDirect: true);
        SetItemVersions(Version(OverCapId, 2160, 40_000_000, "mkv", "hevc"), Version(WithinCapId, 720, 8_000_000));

        var httpContext = await NegotiateAsync(
            BrowserBody.Replace("\"MaxStreamingBitrate\": 4000000,\n  \"DeviceProfile\"", "\"MaxStreamingBitrate\": 4000000, \"MediaSourceId\": \"" + OverCapId + "\",\n  \"DeviceProfile\"", StringComparison.Ordinal));

        Assert.Equal(OverCapId, JsonNode.Parse(ReadBody(httpContext))!["MediaSourceId"]!.GetValue<string>());
        Assert.Equal(4_000_000, BodyCeiling(httpContext));
        Assert.Equal(4_000_000, ProfileCeiling(httpContext));
    }

    [Fact]
    public async Task RequestNamingTheWithinCapVersion_InTheQuery_RaisesToThatVersion()
    {
        UseDirectWithinCapPolicy(keepDirect: true);
        SetItemVersions(
            Version(OverCapId, 2160, 40_000_000, "mkv", "hevc"),
            Version(WithinCapId, 720, 8_000_000),
            Version("cccccccccccccccccccccccccccccccc", 480, 3_000_000));

        var httpContext = await NegotiateAsync(
            BrowserBody,
            new Dictionary<string, string> { ["mediaSourceId"] = WithinCapId });

        Assert.Equal(8_000_000, BodyCeiling(httpContext));
    }

    [Fact]
    public async Task CeilingIsRaisedToTheLargestWithinCapNeed_AndNoFurther()
    {
        UseDirectWithinCapPolicy(keepDirect: true);
        SetItemVersions(
            Version(OverCapId, 1080, 25_000_000),
            Version(WithinCapId, 720, 8_000_000),
            Version("cccccccccccccccccccccccccccccccc", 480, 3_000_000));

        var httpContext = await NegotiateAsync(BrowserBody);

        Assert.Equal(8_000_000, BodyCeiling(httpContext));
        Assert.Equal(8_000_000, ProfileCeiling(httpContext));
    }

    [Fact]
    public async Task CeilingAlreadyAboveTheNeed_IsNeverLowered()
    {
        UseDirectWithinCapPolicy(keepDirect: true);
        SetItemVersions(Version(WithinCapId, 720, 8_000_000));

        var httpContext = await NegotiateAsync(BrowserBody.Replace("4000000", "20000000", StringComparison.Ordinal));

        Assert.Equal(20_000_000, BodyCeiling(httpContext));
        Assert.Equal(20_000_000, ProfileCeiling(httpContext));
    }

    [Fact]
    public async Task VersionWithNoKnownHeight_RaisesNothing()
    {
        UseDirectWithinCapPolicy(keepDirect: true);
        SetItemVersions(Version(WithinCapId, null, 8_000_000));

        var httpContext = await NegotiateAsync(BrowserBody);

        Assert.Equal(4_000_000, BodyCeiling(httpContext));
    }

    [Fact]
    public async Task VideoBitrateCondition_IsRaisedToTheWithinCapStream_SoItDirectPlays()
    {
        const string Body = """
            {
              "DeviceProfile": {
                "DirectPlayProfiles": [ { "Container": "mp4", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac" } ],
                "TranscodingProfiles": [ { "Container": "ts", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac", "Protocol": "hls", "Context": "Streaming" } ],
                "CodecProfiles": [
                  { "Type": "Video", "Codec": "h264", "Conditions": [ { "Condition": "LessThanEqual", "Property": "VideoBitrate", "Value": "3000000", "IsRequired": false } ] }
                ]
              }
            }
            """;
        var withinCap = Version(WithinCapId, 720, 8_000_000, videoBitrate: 7_500_000);
        SetItemVersions(Version(OverCapId, 2160, 40_000_000, "mkv", "hevc", videoBitrate: 39_000_000), withinCap);

        UseDirectWithinCapPolicy(keepDirect: false);
        var before = await NegotiateAsync(Body);
        Assert.Equal(PlayMethod.Transcode, Decide(before, withinCap).PlayMethod);
        Assert.True(Decide(before, withinCap).TranscodeReasons.HasFlag(TranscodeReason.VideoBitrateNotSupported));

        UseDirectWithinCapPolicy(keepDirect: true);
        var after = await NegotiateAsync(Body);
        Assert.Equal(PlayMethod.DirectPlay, Decide(after, withinCap).PlayMethod);

        var condition = JsonNode.Parse(ReadBody(after))!["DeviceProfile"]!["CodecProfiles"]![0]!["Conditions"]![0]!;
        Assert.Equal("7500000", condition["Value"]!.GetValue<string>());
    }

    [Fact]
    public async Task VersionTheUserCannotSee_DoesNotSetTheCeiling()
    {
        // Jellyfin answers with the versions this user can see. When the only within-cap version
        // is hidden from them, the answer is the over-cap version alone, and nothing is raised.
        UseDirectWithinCapPolicy(keepDirect: true);
        var userId = Guid.NewGuid();
        var viewer = new User("viewer", "provider", "reset");
        _userManagerMock.Setup(u => u.GetUserById(userId)).Returns(viewer);
        _libraryManagerMock.Setup(l => l.GetItemById(ItemId)).Returns(new Movie { Id = ItemId, Name = "Show A" });
        _mediaSourceManagerMock
            .Setup(m => m.GetStaticMediaSources(It.IsAny<BaseItem>(), false, It.IsAny<User?>()))
            .Returns(new[] { Version(OverCapId, 2160, 40_000_000, "mkv", "hevc"), Version(WithinCapId, 720, 8_000_000) });
        _mediaSourceManagerMock
            .Setup(m => m.GetStaticMediaSources(It.IsAny<BaseItem>(), false, viewer))
            .Returns(new[] { Version(OverCapId, 2160, 40_000_000, "mkv", "hevc") });

        var httpContext = CreatePlaybackInfoPost(userId, BrowserBody);
        await RunResourceAsync(httpContext);

        Assert.Equal(4_000_000, BodyCeiling(httpContext));
    }

    [Fact]
    public async Task WhenTheVersionsCannotBeRead_TheHeightCapStillReachesTheProfile()
    {
        UseDirectWithinCapPolicy(keepDirect: true);
        _libraryManagerMock.Setup(l => l.GetItemById(ItemId)).Throws(new InvalidOperationException("library unavailable"));

        var httpContext = await NegotiateAsync(BrowserBody);

        var root = JsonNode.Parse(ReadBody(httpContext))!;
        var condition = root["DeviceProfile"]!["CodecProfiles"]![0]!["Conditions"]![0]!;
        Assert.Equal("Height", condition["Property"]!.GetValue<string>());
        Assert.Equal(4_000_000, BodyCeiling(httpContext));
        AssertLoggedAtLeastOnce(LogLevel.Error);
    }

    [Fact]
    public async Task WhenAClientConditionIsMalformed_TheHeightCapStillReachesTheProfile()
    {
        // A duplicate key throws the first time the condition object is read, and the raise
        // steps are the first code to read it. The cap must still be written.
        UseDirectWithinCapPolicy(keepDirect: true);
        SetItemVersions(Version(OverCapId, 2160, 40_000_000, "mkv", "hevc"), Version(WithinCapId, 720, 8_000_000));
        var body = BrowserBody.Replace(
            "\"CodecProfiles\": []",
            "\"CodecProfiles\": [ { \"Type\": \"Video\", \"Conditions\": [ { \"Condition\": \"LessThanEqual\", \"Property\": \"VideoBitrate\", \"Value\": \"1\", \"Value\": \"2\" } ] } ]",
            StringComparison.Ordinal);
        Assert.NotEqual(BrowserBody, body);

        var httpContext = await NegotiateAsync(body);

        var written = ReadBody(httpContext);
        Assert.NotEqual(body, written);
        Assert.Contains("\"Property\":\"Height\"", written, StringComparison.Ordinal);
        Assert.Contains("\"Value\":\"720\"", written, StringComparison.Ordinal);
        AssertLoggedAtLeastOnce(LogLevel.Error);
    }
}
