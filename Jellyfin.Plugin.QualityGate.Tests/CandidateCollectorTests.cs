using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.EncodePriority;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.QualityGate.Tests;

/// <summary>
/// The demand queries, against mocked Jellyfin services. What matters is the shape of each query:
/// the in-process defaults Jellyfin's API layer normally fills in are not filled in here.
/// </summary>
public class CandidateCollectorTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<ITVSeriesManager> _tv = new();
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Mock<IMediaSourceManager> _mediaSources = new();
    private readonly List<InternalItemsQuery> _itemQueries = new();
    private readonly List<NextUpQuery> _nextUpQueries = new();
    private readonly Dictionary<Guid, int?> _heights = new();

    public CandidateCollectorTests()
    {
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(_itemQueries.Add)
            .Returns(new List<BaseItem>());
        _tv.Setup(t => t.GetNextUp(It.IsAny<NextUpQuery>(), It.IsAny<DtoOptions>()))
            .Callback<NextUpQuery, DtoOptions>((q, _) => _nextUpQueries.Add(q))
            .Returns(new QueryResult<BaseItem>());
        _sessions.Setup(s => s.Sessions).Returns(Array.Empty<SessionInfo>());
        _mediaSources.Setup(m => m.GetMediaStreams(It.IsAny<Guid>()))
            .Returns<Guid>(id => new List<MediaStream> { new() { Type = MediaStreamType.Video, Height = _heights.GetValueOrDefault(id) } });
    }

    private CandidateCollector Collector() => new(
        _library.Object,
        _tv.Object,
        _sessions.Object,
        _userData.Object,
        _mediaSources.Object,
        v => new[] { v });

    private static EncodePriorityOptions Options(Action<PluginConfiguration>? edit = null)
    {
        var config = new PluginConfiguration { EnableEncodePriority = true };
        edit?.Invoke(config);
        return EncodePriorityOptions.From(config);
    }

    private static User Viewer(string name, int idleDays = 0)
    {
        return new User(name, "provider", "reset") { LastActivityDate = Now.AddDays(-idleDays) };
    }

    private static Episode EpisodeOf(string showKey, int season, int episode, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = $"{showKey} S{season:00}E{episode:00}",
        Path = $"/media/tv/{showKey}/Season {season}/{showKey} S{season:00}E{episode:00}.mkv",
        SeriesPresentationUniqueKey = showKey,
        ParentIndexNumber = season,
        IndexNumber = episode,
    };

    private void NextUpReturns(params BaseItem[] items)
    {
        _tv.Setup(t => t.GetNextUp(It.Is<NextUpQuery>(q => q.SeriesId == null), It.IsAny<DtoOptions>()))
            .Callback<NextUpQuery, DtoOptions>((q, _) => _nextUpQueries.Add(q))
            .Returns(new QueryResult<BaseItem>(items));
    }

    private void LookaheadReturns(string showKey, params Episode[] episodes)
    {
        _library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.SeriesPresentationUniqueKey == showKey && q.User == null)))
            .Callback<InternalItemsQuery>(_itemQueries.Add)
            .Returns(episodes.Cast<BaseItem>().ToList());
    }

    [Fact]
    public void NextUp_AlwaysAsksForResumableEpisodesWithinTheWindow()
    {
        var a = Viewer("a");
        var b = Viewer("b");

        Collector().Collect(Options(c => c.PriorityWatchedWithinDays = 14), new[] { a, b }, Now, CancellationToken.None);

        Assert.Equal(2, _nextUpQueries.Count);
        Assert.All(_nextUpQueries, q =>
        {
            Assert.True(q.EnableResumable);
            Assert.Equal(Now.AddDays(-14), q.NextUpDateCutoff);
            Assert.Equal(10, q.Limit);
        });
        Assert.Equal(new[] { a.Id, b.Id }, _nextUpQueries.Select(q => q.User.Id));
    }

    [Fact]
    public void Lookahead_UsesThePresentationKeyAndStartsAtTheNextUpEpisode()
    {
        var next = EpisodeOf("show-a", 2, 5);
        var after = EpisodeOf("show-a", 2, 6);
        var later = EpisodeOf("show-a", 3, 1);
        NextUpReturns(next);
        LookaheadReturns("show-a", next, after, later);

        var demand = Collector().Collect(Options(), new[] { Viewer("a") }, Now, CancellationToken.None);

        var lookahead = Assert.Single(_itemQueries, q => q.User == null);
        Assert.Equal("show-a", lookahead.SeriesPresentationUniqueKey);
        Assert.Equal((2, 5), lookahead.MinParentAndIndexNumber);
        Assert.Equal(3, lookahead.Limit);
        Assert.Equal(new[] { BaseItemKind.Episode }, lookahead.IncludeItemTypes);
        Assert.False(lookahead.IsVirtualItem);
        Assert.Equal(0, lookahead.ParentIndexNumberNotEquals);
        Assert.Equal(
            new[] { (ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending) },
            lookahead.OrderBy);

        var depths = demand.Signals.GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Min(s => s.Depth));
        Assert.Equal(1, depths[next.Id]);
        Assert.Equal(2, depths[after.Id]);
        Assert.Equal(3, depths[later.Id]);
        Assert.All(demand.Signals, s => Assert.Equal(DemandTier.NextUp, s.Tier));
    }

    [Fact]
    public void Lookahead_IsSharedBetweenViewersOfTheSameShow()
    {
        var next = EpisodeOf("show-a", 1, 2);
        NextUpReturns(next);
        LookaheadReturns("show-a", next, EpisodeOf("show-a", 1, 3));

        var demand = Collector().Collect(Options(), new[] { Viewer("a"), Viewer("b") }, Now, CancellationToken.None);

        Assert.Single(_itemQueries, q => q.User == null);
        Assert.Equal(2, demand.Signals.Where(s => s.ItemId == next.Id).Select(s => s.UserId).Distinct().Count());
    }

    [Fact]
    public void Lookahead_DepthOne_RunsNoLookaheadQuery()
    {
        var next = EpisodeOf("show-a", 1, 2);
        NextUpReturns(next);

        var demand = Collector().Collect(Options(c => c.PriorityNextUpDepth = 1), new[] { Viewer("a") }, Now, CancellationToken.None);

        Assert.DoesNotContain(_itemQueries, q => q.User == null);
        Assert.Equal(1, Assert.Single(demand.Signals).Depth);
    }

    [Fact]
    public void Specials_AreSkippedUnlessIncluded()
    {
        var special = EpisodeOf("show-a", 0, 1);
        NextUpReturns(special);
        LookaheadReturns("show-a", special);

        var skipped = Collector().Collect(Options(), new[] { Viewer("a") }, Now, CancellationToken.None);
        Assert.Empty(skipped.Signals);

        _itemQueries.Clear();
        var included = Collector().Collect(Options(c => c.PriorityNextUpIncludeSpecials = true), new[] { Viewer("a") }, Now, CancellationToken.None);
        Assert.Contains(included.Signals, s => s.ItemId == special.Id);
        Assert.Null(Assert.Single(_itemQueries, q => q.User == null).ParentIndexNumberNotEquals);
    }

    [Fact]
    public void NowPlaying_ASpecialOrAnEpisodeWithNoSeason_FeedsNoLookahead()
    {
        // With specials excluded, a lookahead from (0, n) would return the show's first regular
        // episodes, long since watched by a viewer mid-series.
        var viewer = Viewer("a");
        var special = EpisodeOf("show-a", 0, 3);
        var noSeason = EpisodeOf("show-b", 1, 4);
        noSeason.ParentIndexNumber = null;
        _sessions.Setup(s => s.Sessions).Returns(new[]
        {
            new SessionInfo(_sessions.Object, Mock.Of<ILogger>()) { UserId = viewer.Id, FullNowPlayingItem = special },
            new SessionInfo(_sessions.Object, Mock.Of<ILogger>()) { UserId = viewer.Id, FullNowPlayingItem = noSeason },
        });

        var demand = Collector().Collect(Options(), new[] { viewer }, Now, CancellationToken.None);

        Assert.DoesNotContain(_itemQueries, q => q.User == null);
        Assert.Contains(demand.Signals, s => s.ItemId == special.Id && s.Tier == DemandTier.NowPlaying);

        _itemQueries.Clear();
        Collector().Collect(Options(c => c.PriorityNextUpIncludeSpecials = true), new[] { viewer }, Now, CancellationToken.None);
        Assert.Equal((0, 4), Assert.Single(_itemQueries, q => q.User == null).MinParentAndIndexNumber);
    }

    [Fact]
    public void ContinueWatching_IsLimitedPerViewerAndOrderedByLastPlayed()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Film A (2001)", Path = "/media/movies/Film A (2001).mkv" };
        var played = Now.AddHours(-3);
        _library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IsResumable == true)))
            .Callback<InternalItemsQuery>(_itemQueries.Add)
            .Returns(new List<BaseItem> { movie });
        _userData.Setup(u => u.GetUserData(It.IsAny<User>(), movie)).Returns(new UserItemData { Key = "k", LastPlayedDate = played });

        var demand = Collector().Collect(Options(c => c.PriorityContinueWatchingPerUser = 7), new[] { Viewer("a") }, Now, CancellationToken.None);

        var query = Assert.Single(_itemQueries, q => q.IsResumable == true);
        Assert.Equal(7, query.Limit);
        Assert.True(query.Recursive);
        Assert.False(query.IsVirtualItem);
        Assert.Equal(new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) }, query.OrderBy);
        Assert.NotNull(query.User);

        // Without it the query is slow and resumable folders crowd videos out of the limit.
        Assert.Equal(new[] { MediaType.Video }, query.MediaTypes);
        var signal = Assert.Single(demand.Signals);
        Assert.Equal((movie.Id, DemandTier.ContinueWatching, 0, played), (signal.ItemId, signal.Tier, signal.Depth, signal.LastActivity));
    }

    [Fact]
    public void NextUp_ShowsPerViewerLimitIsPassedThrough()
    {
        Collector().Collect(Options(c => c.PriorityNextUpShowsPerUser = 4), new[] { Viewer("a") }, Now, CancellationToken.None);

        Assert.Equal(4, Assert.Single(_nextUpQueries).Limit);
    }

    [Fact]
    public void IdleViewers_AreSkippedBeforeAnyQuery()
    {
        var idle = Viewer("idle", idleDays: 31);
        var never = new User("never", "provider", "reset");

        var demand = Collector().Collect(Options(), new[] { idle, never }, Now, CancellationToken.None);

        Assert.Empty(_itemQueries);
        Assert.Empty(_nextUpQueries);
        Assert.Equal(2, demand.UsersSkippedIdle);
        Assert.Equal(0, demand.UsersProcessed);
    }

    [Fact]
    public void SwitchedOffSignals_RunNoQuery()
    {
        var demand = Collector().Collect(
            Options(c =>
            {
                c.PriorityNowPlaying = false;
                c.PriorityContinueWatching = false;
                c.PriorityNextUp = false;
                c.PriorityFavourites = false;
            }),
            new[] { Viewer("a") },
            Now,
            CancellationToken.None);

        Assert.Empty(_itemQueries);
        Assert.Empty(_nextUpQueries);
        _sessions.Verify(s => s.Sessions, Times.Never);
        Assert.Equal(1, demand.UsersProcessed);
    }

    [Fact]
    public void NowPlaying_IsTierZeroAndFeedsTheLookaheadFromTheNextEpisode()
    {
        var viewer = Viewer("a");
        var playing = EpisodeOf("show-a", 2, 5);
        var following = EpisodeOf("show-a", 2, 6);
        var session = new SessionInfo(_sessions.Object, Mock.Of<ILogger>()) { UserId = viewer.Id, FullNowPlayingItem = playing };
        var other = new SessionInfo(_sessions.Object, Mock.Of<ILogger>()) { UserId = Guid.NewGuid(), FullNowPlayingItem = EpisodeOf("show-b", 1, 1) };
        _sessions.Setup(s => s.Sessions).Returns(new[] { session, other });
        LookaheadReturns("show-a", following);

        var demand = Collector().Collect(Options(), new[] { viewer }, Now, CancellationToken.None);

        Assert.Contains(demand.Signals, s => s.ItemId == playing.Id && s.Tier == DemandTier.NowPlaying && s.Depth == 0);
        Assert.Contains(demand.Signals, s => s.ItemId == following.Id && s.Tier == DemandTier.NextUp && s.Depth == 1);
        Assert.Equal((2, 6), Assert.Single(_itemQueries, q => q.User == null).MinParentAndIndexNumber);
        Assert.DoesNotContain(demand.Signals, s => s.UserId != viewer.Id);
    }

    [Fact]
    public void Favourites_AreLimitedPerViewer_MoviesDirectly_ShowsFromTheirNextUp()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Film A (2001)", Path = "/media/movies/Film A (2001).mkv" };
        var series = new Series { Id = Guid.NewGuid(), Name = "Show B", PresentationUniqueKey = "show-b" };
        var start = EpisodeOf("show-b", 1, 1);
        _library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IsFavorite == true)))
            .Callback<InternalItemsQuery>(_itemQueries.Add)
            .Returns(new List<BaseItem> { movie, series });
        _tv.Setup(t => t.GetNextUp(It.Is<NextUpQuery>(q => q.SeriesId == series.Id), It.IsAny<DtoOptions>()))
            .Returns(new QueryResult<BaseItem>(new BaseItem[] { start }));
        LookaheadReturns("show-b", start, EpisodeOf("show-b", 1, 2));

        var demand = Collector().Collect(
            Options(c =>
            {
                c.PriorityFavourites = true;
                c.PriorityFavouritesPerUser = 9;
                c.PriorityNextUp = false;
            }),
            new[] { Viewer("a") },
            Now,
            CancellationToken.None);

        Assert.Equal(9, Assert.Single(_itemQueries, q => q.IsFavorite == true).Limit);
        Assert.Contains(demand.Signals, s => s.ItemId == movie.Id && s.Tier == DemandTier.Favourite && s.Depth == 0);
        Assert.Contains(demand.Signals, s => s.ItemId == start.Id && s.Tier == DemandTier.Favourite && s.Depth == 1);
        Assert.Equal(3, demand.Signals.Select(s => s.ItemId).Distinct().Count());
    }

    [Fact]
    public void Versions_AreMeasuredOncePerVersionTheWayPlaybackMeasuresThem()
    {
        var next = EpisodeOf("show-a", 1, 2);
        _heights[next.Id] = 1080;
        NextUpReturns(next);
        LookaheadReturns("show-a", next);

        var demand = Collector().Collect(Options(), new[] { Viewer("a"), Viewer("b") }, Now, CancellationToken.None);

        var version = Assert.Single(demand.Versions[next.Id]);
        Assert.Equal(new VersionInfo(next.Id, next.Path, 1080), version);
        _mediaSources.Verify(m => m.GetMediaStreams(next.Id), Times.Once);
    }

    [Fact]
    public void Cancellation_StopsBetweenViewers()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Collector().Collect(Options(), new[] { Viewer("a") }, Now, cts.Token));
        Assert.Empty(_nextUpQueries);
    }
}
