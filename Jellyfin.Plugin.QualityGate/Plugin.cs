using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QualityGate;

/// <summary>
/// Quality Gate plugin for Jellyfin.
/// Restricts users to specific media versions based on policies.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private ILogger<Plugin>? _logger;
    private ISessionManager? _sessionManager;
    private ILibraryManager? _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Sets up the session manager for playback monitoring.
    /// Called by the entry point after DI is ready.
    /// </summary>
    public void SetupSessionManager(ISessionManager sessionManager, ILibraryManager libraryManager, ILogger<Plugin> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _logger = logger;
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _logger?.LogInformation("QualityGate: Plugin initialized, monitoring playback sessions. Middleware provides version filtering.");
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var session = e.Session;
            var userId = session?.UserId ?? Guid.Empty;
            
            if (userId == Guid.Empty)
            {
                return;
            }

            var policy = QualityGateService.GetUserPolicy(userId);
            if (policy == null)
            {
                _logger?.LogDebug("QualityGate: No policy for user {UserId}", userId);
                return;
            }

            // Get the file path from the playing item
            string? filePath = e.Item?.Path ?? e.MediaInfo?.Path;
            var currentItem = e.Item;

            if (string.IsNullOrEmpty(filePath))
            {
                _logger?.LogDebug("QualityGate: Could not determine file path for playback");
                return;
            }

            _logger?.LogDebug("QualityGate: Checking access for user {UserId} to path {Path}", userId, filePath);

            var isAllowed = QualityGateService.IsPathAllowed(policy, filePath);

            if (!isAllowed)
            {
                _logger?.LogWarning(
                    "QualityGate: BLOCKING playback for user {UserId} (policy: {PolicyName}) - Path: {Path}",
                    userId, policy.Name, filePath);

                // Stop playback and try to find/redirect to an allowed version
                Task.Run(async () =>
                {
                    try
                    {
                        if (_sessionManager != null && session != null)
                        {
                            // Stop the blocked playback
                            await _sessionManager.SendPlaystateCommand(
                                session.Id,
                                session.Id,
                                new PlaystateRequest { Command = PlaystateCommand.Stop },
                                default).ConfigureAwait(false);

                            // Try to find an allowed alternate version
                            BaseItem? allowedVersion = null;
                            
                            if (currentItem != null && _libraryManager != null)
                            {
                                allowedVersion = FindAllowedVersion(currentItem, policy);
                            }
                            
                            if (allowedVersion != null)
                            {
                                _logger?.LogInformation(
                                    "QualityGate: Auto-fallback to allowed version: {Path}",
                                    allowedVersion.Path);

                                // Small delay to let the stop command process
                                await Task.Delay(500).ConfigureAwait(false);

                                // Start playback of the allowed version
                                await _sessionManager.SendPlayCommand(
                                    session.Id,
                                    session.Id,
                                    new PlayRequest
                                    {
                                        ItemIds = new[] { allowedVersion.Id },
                                        PlayCommand = PlayCommand.PlayNow
                                    },
                                    default).ConfigureAwait(false);
                            }
                            else
                            {
                                // No allowed version found - show configurable message
                                _logger?.LogWarning(
                                    "QualityGate: No allowed version found for blocked item");

                                await _sessionManager.SendMessageCommand(
                                    session.Id,
                                    session.Id,
                                    new MessageCommand
                                    {
                                        Header = policy.BlockedMessageHeader,
                                        Text = policy.BlockedMessageText,
                                        TimeoutMs = policy.BlockedMessageTimeoutMs
                                    },
                                    default).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "QualityGate: Failed to handle blocked playback");
                    }
                });
            }
            else
            {
                _logger?.LogDebug("QualityGate: Allowed playback for user {UserId} - Path: {Path}", userId, filePath);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "QualityGate: Error in playback start handler");
        }
    }

    /// <summary>
    /// Finds an allowed alternate version of the given item based on the policy.
    /// Searches for items with similar name patterns that match allowed paths.
    /// </summary>
    private BaseItem? FindAllowedVersion(BaseItem item, QualityPolicy policy)
    {
        try
        {
            if (_libraryManager == null)
            {
                return null;
            }

            var originalPath = item.Path;
            if (string.IsNullOrEmpty(originalPath))
            {
                return null;
            }

            // Try to find the allowed version by constructing the expected path
            // Based on allowed/blocked prefixes, we can find the corresponding version
            foreach (var allowedPrefix in policy.AllowedPathPrefixes)
            {
                foreach (var blockedPrefix in policy.BlockedPathPrefixes)
                {
                    if (!originalPath.StartsWith(blockedPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Get the relative path after the blocked prefix
                    var relativePath = originalPath.Substring(blockedPrefix.TrimEnd('/').Length);
                    
                    // Construct the potential allowed path
                    var allowedPath = allowedPrefix.TrimEnd('/') + relativePath;
                    
                    _logger?.LogDebug(
                        "QualityGate: Looking for allowed version at: {AllowedPath}",
                        allowedPath);

                    // Search for an item with this path
                    var query = new InternalItemsQuery
                    {
                        Path = allowedPath,
                        IsVirtualItem = false,
                        Limit = 1
                    };

                    var matches = _libraryManager.GetItemList(query);
                    if (matches.Count > 0)
                    {
                        var match = matches[0];
                        if (QualityGateService.IsPathAllowed(policy, match.Path))
                        {
                            _logger?.LogDebug(
                                "QualityGate: Found allowed version: {MatchPath}",
                                match.Path);
                            return match;
                        }
                    }

                    // Also try with quality suffix variations
                    var fileName = System.IO.Path.GetFileName(originalPath);
                    var directory = System.IO.Path.GetDirectoryName(allowedPath) ?? string.Empty;
                    
                    // Try common quality suffix replacements
                    var suffixReplacements = new[]
                    {
                        (" - 1080p", " - 720p"),
                        (" - 4K", " - 720p"),
                        (" - 2160p", " - 720p"),
                        ("1080p", "720p"),
                        ("4K", "720p"),
                        ("2160p", "720p")
                    };

                    foreach (var (from, to) in suffixReplacements)
                    {
                        if (fileName.Contains(from, StringComparison.OrdinalIgnoreCase))
                        {
                            var newFileName = fileName.Replace(from, to, StringComparison.OrdinalIgnoreCase);
                            var altPath = System.IO.Path.Combine(directory, newFileName);
                            
                            var altQuery = new InternalItemsQuery
                            {
                                Path = altPath,
                                IsVirtualItem = false,
                                Limit = 1
                            };

                            var altMatches = _libraryManager.GetItemList(altQuery);
                            if (altMatches.Count > 0 && QualityGateService.IsPathAllowed(policy, altMatches[0].Path))
                            {
                                return altMatches[0];
                            }
                        }
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "QualityGate: Error finding allowed version");
            return null;
        }
    }

    /// <inheritdoc />
    public override string Name => "Quality Gate";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    /// <inheritdoc />
    public override string Description => "Restrict users to specific media versions. Hides blocked versions and auto-falls back to allowed ones.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            },
            new PluginPageInfo
            {
                Name = "configPage.js",
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.js"
            }
        };
    }
}
