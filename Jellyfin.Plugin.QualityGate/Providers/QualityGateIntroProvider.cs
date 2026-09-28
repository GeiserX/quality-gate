using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QualityGate.Providers;

/// <summary>
/// Provides custom intro videos based on user quality policies.
/// When a user is under a policy with a custom intro path, that intro is
/// added to Jellyfin's intro list. Note: Jellyfin aggregates all registered
/// IIntroProvider results, so if the built-in "Local Intros" plugin is also
/// enabled its intros will play in addition to this one. Disable "Local Intros"
/// if you only want Quality Gate intros.
/// </summary>
public class QualityGateIntroProvider : IIntroProvider
{
    private readonly ILogger<QualityGateIntroProvider> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IUserDataManager _userDataManager;

    /// <summary>
    /// Cache of intro paths → DB item IDs to avoid redundant lookups/registrations.
    /// Also used by the result filter to skip filtering for intro video playback.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Guid> _introIdCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tracks which (userId, seriesId) pairs have already seen the intro this session.
    /// For shows, the intro plays only on the first episode; this cache ensures subsequent
    /// episodes skip it even before Jellyfin's UserData is updated.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> _seriesIntroShown = new();

    /// <summary>
    /// Checks whether an item ID belongs to a registered intro video.
    /// Used by <see cref="Filters.MediaSourceResultFilter"/> to skip policy
    /// filtering on intro playback requests.
    /// </summary>
    public static bool IsRegisteredIntro(Guid itemId)
    {
        return _introIdCache.Values.Contains(itemId);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="QualityGateIntroProvider"/> class.
    /// </summary>
    public QualityGateIntroProvider(
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        IMediaSourceManager mediaSourceManager,
        IUserDataManager userDataManager)
    {
        _logger = loggerFactory.CreateLogger<QualityGateIntroProvider>();
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _mediaSourceManager = mediaSourceManager;
        _userDataManager = userDataManager;
    }

    /// <inheritdoc />
    public string Name => "QualityGate Intros";

    /// <inheritdoc />
    public Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
    {
        var result = Enumerable.Empty<IntroInfo>();

        try
        {
            if (user == null)
            {
                _logger.LogDebug("QualityGateIntroProvider: No user provided, skipping");
                return Task.FromResult(result);
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogDebug("QualityGateIntroProvider: No configuration available");
                return Task.FromResult(result);
            }

            string? introPath = null;
            string source = "default";

            // Check if user has a policy with custom intro
            var policy = QualityGateService.GetUserPolicy(user.Id);

            // A policy that is missing or disabled refuses all playback, so an intro would play and
            // then the item would be refused. Skip it without looking at the item.
            if (QualityGateService.IsDenyAll(policy))
            {
                _logger.LogDebug(
                    "QualityGateIntroProvider: Skipping intro — user {UserName} is on a policy that is missing or disabled",
                    user.Username);
                return Task.FromResult(result);
            }

            // If user is restricted and ALL sources for this item are blocked, skip the intro
            // to prevent double-error UX (intro plays → then content denied)
            if (policy != null && item != null)
            {
                try
                {
                    var sources = _mediaSourceManager.GetStaticMediaSources(item, false);
                    if (sources.Count > 0 && !sources.Any(s => QualityGateService.IsSourcePlayable(policy, s.Path))
                        && !QualityGateService.ShouldFallbackTranscode(policy, sources))
                    {
                        _logger.LogDebug(
                            "QualityGateIntroProvider: Skipping intro — all sources blocked for user {UserName}",
                            user.Username);
                        return Task.FromResult(result);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "QualityGateIntroProvider: Error checking sources, proceeding with intro");
                }
            }

            // Skip intro if user is resuming or has already seen it for this show
            if (item != null && ShouldSkipIntro(item, user))
            {
                return Task.FromResult(result);
            }

            if (policy != null && !string.IsNullOrWhiteSpace(policy.IntroVideoPath))
            {
                introPath = policy.IntroVideoPath;
                source = $"policy '{policy.Name}'";
            }
            // Fall back to default intro
            else if (!string.IsNullOrWhiteSpace(config.DefaultIntroVideoPath))
            {
                introPath = config.DefaultIntroVideoPath;
                source = "global default";
            }

            if (string.IsNullOrWhiteSpace(introPath))
            {
                _logger.LogDebug("QualityGateIntroProvider: No intro configured for user {UserName}", user.Username);
                return Task.FromResult(result);
            }

            // Check if the intro file exists; fall back to default if policy intro is missing
            if (!File.Exists(introPath))
            {
                _logger.LogWarning("QualityGateIntroProvider: Intro file not found: {IntroPath}", introPath);
                if (source != "global default" && !string.IsNullOrWhiteSpace(config.DefaultIntroVideoPath)
                    && File.Exists(config.DefaultIntroVideoPath))
                {
                    introPath = config.DefaultIntroVideoPath;
                    source = "global default (fallback)";
                    _logger.LogInformation("QualityGateIntroProvider: Falling back to default intro: {IntroPath}", introPath);
                }
                else
                {
                    return Task.FromResult(result);
                }
            }

            _logger.LogInformation(
                "QualityGateIntroProvider: User {UserName} gets intro from {Source}: {IntroPath}",
                user.Username, source, introPath);

            // Jellyfin's ResolveIntro requires the video to exist in the database.
            // It calls ResolvePath → GetItemById, and returns null if not found.
            // We must register the intro video in the DB on first use.
            var videoId = EnsureIntroRegistered(introPath);
            if (videoId == null)
            {
                _logger.LogWarning("QualityGateIntroProvider: Failed to register intro in DB: {IntroPath}", introPath);
                return Task.FromResult(result);
            }

            return Task.FromResult<IEnumerable<IntroInfo>>(new[]
            {
                new IntroInfo { ItemId = videoId.Value }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QualityGateIntroProvider: Error getting intros");
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// Determines whether the intro should be skipped for this playback.
    /// Movies: skip if user is resuming (has playback progress).
    /// Episodes: skip if the user has played or left mid-way any episode of the series, in any
    /// season, or has already been shown the intro for it since the server started.
    /// </summary>
    private bool ShouldSkipIntro(BaseItem item, User user)
    {
        try
        {
            // Skip if user has already watched or is resuming this item
            var userData = _userDataManager.GetUserData(user, item);
            if (userData?.PlaybackPositionTicks > 0)
            {
                _logger.LogDebug(
                    "QualityGateIntroProvider: Skipping intro — user {UserName} is resuming {ItemName}",
                    user.Username, item.Name);
                return true;
            }

            if (userData?.Played == true)
            {
                _logger.LogDebug(
                    "QualityGateIntroProvider: Skipping intro — user {UserName} already watched {ItemName}",
                    user.Username, item.Name);
                return true;
            }

            // For episodes: only show the intro once per entire show
            if (item is Episode episode)
            {
                if (string.IsNullOrEmpty(episode.SeriesPresentationUniqueKey) && episode.SeriesId == Guid.Empty)
                {
                    // No show identity at all: nothing to remember it under, and an all-zero key
                    // would make one such episode silence every other. Show the intro.
                    return false;
                }

                // A library that keeps one folder per season at its root gets one Series item
                // per folder, all merged in the UI through a shared presentation key. Keying on
                // SeriesId would make "once per show" mean "once per season folder", so the key
                // is the show-wide one whenever the episode carries it.
                var showKey = string.IsNullOrEmpty(episode.SeriesPresentationUniqueKey)
                    ? episode.SeriesId.ToString("N")
                    : episode.SeriesPresentationUniqueKey;
                var cacheKey = $"{user.Id}:{showKey}";

                // Check session cache first (covers the gap before Jellyfin updates UserData)
                if (_seriesIntroShown.ContainsKey(cacheKey))
                {
                    _logger.LogDebug(
                        "QualityGateIntroProvider: Skipping intro — user {UserName} already saw intro for show {ShowKey} this session",
                        user.Username, showKey);
                    return true;
                }

                // Persistent state: has the user played, or left mid-way, any episode of this
                // show? This survives a server restart, which the cache above does not.
                if (HasWatchedAnyEpisode(user, episode))
                {
                    _seriesIntroShown.TryAdd(cacheKey, true);
                    _logger.LogDebug(
                        "QualityGateIntroProvider: Skipping intro — user {UserName} has play history for show {ShowKey}",
                        user.Username, showKey);
                    return true;
                }

                // First time watching this show — show intro, mark in session cache
                _seriesIntroShown.TryAdd(cacheKey, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "QualityGateIntroProvider: Error checking skip state, showing intro");
        }

        return false;
    }

    /// <summary>
    /// Whether the user has played, or stopped part-way through, any episode of the show, in any
    /// season and whichever Series item that season's folder produced.
    /// </summary>
    /// <remarks>
    /// This used to read <c>LastPlayedDate</c> on the Series item's own user data. Jellyfin never
    /// writes that: playback stamps the episode, not the show, so the check never matched and
    /// the intro was held back only by the in-memory cache, which every server restart cleared.
    /// Asking the library for one played or one resumable episode of the show is what Jellyfin
    /// itself does for "Next Up", and it is true across seasons, season folders and restarts.
    /// </remarks>
    private bool HasWatchedAnyEpisode(User user, Episode episode)
    {
        foreach (var resumable in new[] { false, true })
        {
            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true,
                Limit = 1,
            };

            // The presentation key spans every Series item of the show, whatever folder each
            // season lives in; SeriesId is the fallback for an episode that carries none.
            if (!string.IsNullOrEmpty(episode.SeriesPresentationUniqueKey))
            {
                query.SeriesPresentationUniqueKey = episode.SeriesPresentationUniqueKey;
            }
            else
            {
                query.AncestorIds = new[] { episode.SeriesId };
            }

            if (resumable)
            {
                query.IsResumable = true;
            }
            else
            {
                query.IsPlayed = true;
            }

            if (_libraryManager.GetItemList(query).Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ensures an intro video file is registered in Jellyfin's database.
    /// Jellyfin's ResolveIntro calls ResolvePath then GetItemById — if the video
    /// isn't in the DB, it silently discards the intro. We mirror that flow and
    /// call CreateItem when the video is missing.
    /// </summary>
    private Guid? EnsureIntroRegistered(string introPath)
    {
        // Check cache first
        if (_introIdCache.TryGetValue(introPath, out var cachedId))
        {
            return cachedId;
        }

        try
        {
            var fileInfo = _fileSystem.GetFileSystemInfo(introPath);
            var resolved = _libraryManager.ResolvePath(fileInfo);
            if (resolved is not Video video)
            {
                _logger.LogWarning("QualityGateIntroProvider: ResolvePath did not return a Video for {Path}", introPath);
                return null;
            }

            var existing = _libraryManager.GetItemById(video.Id);
            if (existing is Video)
            {
                _introIdCache[introPath] = video.Id;
                return video.Id;
            }

            // Not in DB — register it
            _libraryManager.CreateItem(video, null);
            _introIdCache[introPath] = video.Id;
            _logger.LogInformation("QualityGateIntroProvider: Registered intro video in DB: {Path} (Id: {Id})", introPath, video.Id);
            return video.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QualityGateIntroProvider: Error registering intro {Path}", introPath);
            return null;
        }
    }
}

