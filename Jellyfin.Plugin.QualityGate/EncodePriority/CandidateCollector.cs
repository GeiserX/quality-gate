using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.QualityGate.EncodePriority;

/// <summary>How strong a piece of evidence is that a viewer will open an item soon. Lower ranks first.</summary>
internal static class DemandTier
{
    /// <summary>Playing right now.</summary>
    public const int NowPlaying = 0;

    /// <summary>Resumable.</summary>
    public const int ContinueWatching = 1;

    /// <summary>The next episodes of a show in progress.</summary>
    public const int NextUp = 2;

    /// <summary>A favourite movie, or the next episodes of a favourite show.</summary>
    public const int Favourite = 3;

    /// <summary>Gets the reason shown for a tier.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>A short label.</returns>
    public static string Reason(int tier) => tier switch
    {
        NowPlaying => "playing",
        ContinueWatching => "continue watching",
        NextUp => "next up",
        _ => "favourite",
    };
}

/// <summary>One piece of evidence that a user will watch an item.</summary>
/// <param name="ItemId">The item.</param>
/// <param name="UserId">The viewer.</param>
/// <param name="Tier">See <see cref="DemandTier"/>.</param>
/// <param name="Depth">0 for items that are not a lookahead; 1 for the next-up episode, 2 and more further ahead.</param>
/// <param name="LastActivity">When the viewer last showed this interest, for ordering.</param>
internal sealed record DemandSignal(Guid ItemId, Guid UserId, int Tier, int Depth, DateTime LastActivity);

/// <summary>One version of an item, measured the way playback measures it.</summary>
/// <param name="Id">The version's item id, which is also its media source id.</param>
/// <param name="Path">The file as Jellyfin sees it.</param>
/// <param name="Height">The tallest video stream's height, or null when never probed.</param>
internal sealed record VersionInfo(Guid Id, string Path, int? Height);

/// <summary>What one run found capped viewers are watching.</summary>
internal sealed class CollectedDemand
{
    /// <summary>Gets every signal, possibly several per item.</summary>
    public List<DemandSignal> Signals { get; } = new();

    /// <summary>Gets every version of every item a signal names.</summary>
    public Dictionary<Guid, IReadOnlyList<VersionInfo>> Versions { get; } = new();

    /// <summary>Gets or sets how many viewers were queried.</summary>
    public int UsersProcessed { get; set; }

    /// <summary>Gets or sets how many viewers were skipped for being idle longer than the window.</summary>
    public int UsersSkippedIdle { get; set; }
}

/// <summary>Finds what capped viewers are watching.</summary>
internal interface ICandidateCollector
{
    /// <summary>Queries each viewer's demand and measures every version of every item found.</summary>
    /// <param name="options">The settings.</param>
    /// <param name="users">The viewers to query; idle ones are skipped.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <param name="cancellationToken">Stops the run between viewers and between items.</param>
    /// <param name="into">The demand to fill, so a caller still sees how far a cancelled run got.</param>
    /// <returns>The demand.</returns>
    CollectedDemand Collect(EncodePriorityOptions options, IReadOnlyList<User> users, DateTime nowUtc, CancellationToken cancellationToken, CollectedDemand? into = null);

    /// <summary>Measures every version of one item, for the check that a listed item is now covered.</summary>
    /// <param name="itemId">The item.</param>
    /// <returns>Its versions, or none when it is gone or not a video.</returns>
    IReadOnlyList<VersionInfo> GetVersions(Guid itemId);
}

/// <summary>
/// Finds what capped viewers are watching, through the same services Jellyfin's own home screen uses.
/// </summary>
/// <remarks>
/// Every per-user query carries the user, so each user's library access and parental rules
/// apply. The lookahead into a show runs without a user and is shared between viewers; it only
/// orders encodes and never shows anything to anyone. Nothing here scans the library.
/// </remarks>
internal sealed class CandidateCollector : ICandidateCollector
{
    private readonly ILibraryManager _libraryManager;
    private readonly ITVSeriesManager _tvSeriesManager;
    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly Func<Video, IReadOnlyList<Video>> _versionsOf;

    /// <summary>Initializes a new instance of the <see cref="CandidateCollector"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="tvSeriesManager">The TV series manager.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="versionsOf">Every version of a video; <see cref="Video.GetAllVersions"/> unless a test says otherwise.</param>
    public CandidateCollector(
        ILibraryManager libraryManager,
        ITVSeriesManager tvSeriesManager,
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        IMediaSourceManager mediaSourceManager,
        Func<Video, IReadOnlyList<Video>>? versionsOf = null)
    {
        _libraryManager = libraryManager;
        _tvSeriesManager = tvSeriesManager;
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _mediaSourceManager = mediaSourceManager;
        _versionsOf = versionsOf ?? (v => v.GetAllVersions());
    }

    /// <summary>Whether a viewer has been idle for longer than the window.</summary>
    /// <param name="lastActivity">The viewer's last activity.</param>
    /// <param name="cutoffUtc">The start of the window.</param>
    /// <returns>True when the viewer is idle.</returns>
    internal static bool IsIdle(DateTime? lastActivity, DateTime cutoffUtc) => !lastActivity.HasValue || lastActivity.Value < cutoffUtc;

    /// <inheritdoc />
    public CollectedDemand Collect(EncodePriorityOptions options, IReadOnlyList<User> users, DateTime nowUtc, CancellationToken cancellationToken, CollectedDemand? into = null)
    {
        var demand = into ?? new CollectedDemand();
        var cutoff = nowUtc.AddDays(-options.WatchedWithinDays);
        var run = new Run(this, options, demand);
        var sessions = options.NowPlaying ? _sessionManager.Sessions.ToList() : new List<SessionInfo>();

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsIdle(user.LastActivityDate, cutoff))
            {
                demand.UsersSkippedIdle++;
                continue;
            }

            demand.UsersProcessed++;
            var userActivity = user.LastActivityDate ?? nowUtc;

            if (options.NowPlaying)
            {
                foreach (var session in sessions.Where(s => s.UserId == user.Id))
                {
                    if (session.FullNowPlayingItem is not Video playing)
                    {
                        continue;
                    }

                    run.Add(playing, user, DemandTier.NowPlaying, 0, nowUtc);
                    if (options.NextUp && playing is Episode episode && episode.IndexNumber is int number && run.LookaheadSeason(episode) is int season)
                    {
                        // What comes after the episode on screen is the next thing this viewer opens.
                        run.AddLookahead(episode, (season, number + 1), user, DemandTier.NextUp, nowUtc, firstDepth: 1);
                    }
                }
            }

            if (options.ContinueWatching)
            {
                // Video only, as the home screen asks. Without it the query takes seconds per
                // viewer and resumable folders and audiobooks fill the limit before any video.
                var resumable = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IsResumable = true,
                    MediaTypes = new[] { MediaType.Video },
                    Recursive = true,
                    IsVirtualItem = false,
                    OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
                    Limit = options.ContinueWatchingPerUser,
                });
                foreach (var item in resumable.OfType<Video>())
                {
                    var played = _userDataManager.GetUserData(user, item)?.LastPlayedDate ?? userActivity;
                    run.Add(item, user, DemandTier.ContinueWatching, 0, played);
                }
            }

            if (options.NextUp)
            {
                var nextUp = _tvSeriesManager.GetNextUp(
                    new NextUpQuery
                    {
                        User = user,
                        EnableResumable = true,
                        NextUpDateCutoff = cutoff,
                        Limit = options.NextUpShowsPerUser,
                        EnableTotalRecordCount = false,
                    },
                    new DtoOptions(false));
                foreach (var episode in (nextUp?.Items ?? Array.Empty<BaseItem>()).OfType<Episode>())
                {
                    run.AddFrom(episode, user, DemandTier.NextUp, userActivity);
                }
            }

            if (options.Favourites)
            {
                var favourites = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IsFavorite = true,
                    Recursive = true,
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                    Limit = options.FavouritesPerUser,
                });
                foreach (var favourite in favourites)
                {
                    if (favourite is Movie movie)
                    {
                        run.Add(movie, user, DemandTier.Favourite, 0, userActivity);
                    }
                    else if (favourite is Series series && FirstEpisodeToWatch(series, user, options) is { } start)
                    {
                        run.AddFrom(start, user, DemandTier.Favourite, userActivity);
                    }
                }
            }
        }

        foreach (var (id, video) in run.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            demand.Versions[id] = Measure(video, run.HeightCache);
        }

        return demand;
    }

    /// <inheritdoc />
    public IReadOnlyList<VersionInfo> GetVersions(Guid itemId)
    {
        return _libraryManager.GetItemById(itemId) is Video video
            ? Measure(video, new Dictionary<Guid, int?>())
            : Array.Empty<VersionInfo>();
    }

    /// <summary>The episode a favourite show starts from: its next-up episode, or else its first unplayed one.</summary>
    private Episode? FirstEpisodeToWatch(Series series, User user, EncodePriorityOptions options)
    {
        var nextUp = _tvSeriesManager.GetNextUp(
            new NextUpQuery
            {
                User = user,
                SeriesId = series.Id,
                EnableResumable = true,
                Limit = 1,
                EnableTotalRecordCount = false,
            },
            new DtoOptions(false));
        if (nextUp?.Items?.OfType<Episode>().FirstOrDefault() is { } episode)
        {
            return episode;
        }

        return _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            SeriesPresentationUniqueKey = series.GetPresentationUniqueKey(),
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsPlayed = false,
            IsVirtualItem = false,
            Recursive = true,
            ParentIndexNumberNotEquals = options.NextUpIncludeSpecials ? null : 0,
            OrderBy = new[] { (ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending) },
            Limit = 1,
        }).OfType<Episode>().FirstOrDefault();
    }

    private IReadOnlyList<VersionInfo> Measure(Video video, Dictionary<Guid, int?> heightCache)
    {
        IReadOnlyList<Video> versions;
        try
        {
            versions = _versionsOf(video);
        }
        catch (InvalidOperationException)
        {
            versions = Array.Empty<Video>();
        }

        if (versions.Count == 0)
        {
            versions = new[] { video };
        }

        var result = new List<VersionInfo>(versions.Count);
        foreach (var version in versions)
        {
            if (string.IsNullOrEmpty(version.Path))
            {
                continue;
            }

            if (!heightCache.TryGetValue(version.Id, out var height))
            {
                // Measured exactly as playback measures it.
                height = QualityGateService.GetVideoHeight(_mediaSourceManager.GetMediaStreams(version.Id));
                heightCache[version.Id] = height;
            }

            result.Add(new VersionInfo(version.Id, version.Path, height));
        }

        return result;
    }

    /// <summary>The state of one collection pass: the items found and the lookahead cache.</summary>
    private sealed class Run
    {
        private readonly CandidateCollector _owner;
        private readonly EncodePriorityOptions _options;
        private readonly CollectedDemand _demand;
        private readonly Dictionary<(string Key, int Season, int Episode, int Depth), IReadOnlyList<Episode>> _lookahead = new();

        public Run(CandidateCollector owner, EncodePriorityOptions options, CollectedDemand demand)
        {
            _owner = owner;
            _options = options;
            _demand = demand;
        }

        public Dictionary<Guid, Video> Items { get; } = new();

        public Dictionary<Guid, int?> HeightCache { get; } = new();

        public void Add(Video item, User user, int tier, int depth, DateTime lastActivity)
        {
            Items.TryAdd(item.Id, item);
            _demand.Signals.Add(new DemandSignal(item.Id, user.Id, tier, depth, lastActivity));
        }

        /// <summary>Adds a starting episode as depth 1 and the episodes after it, up to the configured depth.</summary>
        public void AddFrom(Episode start, User user, int tier, DateTime lastActivity)
        {
            if (!_options.NextUpIncludeSpecials && start.ParentIndexNumber == 0)
            {
                return;
            }

            // The start is depth 1 whatever the lookahead finds; the lookahead lists it again,
            // which the builder merges.
            Add(start, user, tier, 1, lastActivity);
            if (_options.NextUpDepth > 1 && start.IndexNumber is int number && LookaheadSeason(start) is int season)
            {
                AddLookahead(start, (season, number), user, tier, lastActivity, firstDepth: 1);
            }
        }

        /// <summary>
        /// The season a lookahead may start from, or null when none may run from this episode.
        /// </summary>
        /// <remarks>
        /// A special (season 0) is skipped unless specials are included: with season 0 excluded
        /// from the query, a lookahead from (0, n) returns the show's first regular episodes,
        /// which a viewer mid-series watched long ago. An episode with no season number has no
        /// position to look ahead from, for the same reason.
        /// </remarks>
        public int? LookaheadSeason(Episode episode)
            => episode.ParentIndexNumber is int season && (season != 0 || _options.NextUpIncludeSpecials) ? season : null;

        /// <summary>Adds up to the configured depth of episodes from a position in a show.</summary>
        /// <returns>False when the show has no presentation key, so no lookahead could run.</returns>
        public bool AddLookahead(Episode from, (int Season, int Episode) position, User user, int tier, DateTime lastActivity, int firstDepth)
        {
            // The presentation key, never SeriesId: a library with one folder per season gives one
            // show several Series items, and SeriesId would stop the lookahead at a season's end.
            var key = from.SeriesPresentationUniqueKey;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            var depth = _options.NextUpDepth;
            var cacheKey = (key, position.Season, position.Episode, depth);
            if (!_lookahead.TryGetValue(cacheKey, out var episodes))
            {
                episodes = _owner._libraryManager.GetItemList(new InternalItemsQuery
                {
                    SeriesPresentationUniqueKey = key,
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    MinParentAndIndexNumber = position,
                    ParentIndexNumberNotEquals = _options.NextUpIncludeSpecials ? null : 0,
                    OrderBy = new[] { (ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending) },
                    Limit = depth,
                    IsVirtualItem = false,
                    Recursive = true,
                    DtoOptions = new DtoOptions(false),
                }).OfType<Episode>().ToList();
                _lookahead[cacheKey] = episodes;
            }

            for (var i = 0; i < episodes.Count; i++)
            {
                Add(episodes[i], user, tier, firstDepth + i, lastActivity);
            }

            return true;
        }
    }
}
