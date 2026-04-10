using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.QualityGate.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="QualityGateIntroProvider"/> class.
    /// </summary>
    /// <param name="loggerFactory">Logger factory instance.</param>
    public QualityGateIntroProvider(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<QualityGateIntroProvider>();
    }

    /// <inheritdoc />
    public string Name => "Quality Gate Intros";

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

            // If user is restricted and the item's primary path is blocked, skip the intro
            // to prevent double-error UX (intro fails → then content denied)
            if (policy != null && !string.IsNullOrEmpty(item?.Path) && !QualityGateService.IsPathAllowed(policy, item.Path))
            {
                _logger.LogDebug(
                    "QualityGateIntroProvider: Skipping intro — item blocked for user {UserName}",
                    user.Username);
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

            return Task.FromResult<IEnumerable<IntroInfo>>(new[]
            {
                new IntroInfo { Path = introPath }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QualityGateIntroProvider: Error getting intros");
        }

        return Task.FromResult(result);
    }
}

