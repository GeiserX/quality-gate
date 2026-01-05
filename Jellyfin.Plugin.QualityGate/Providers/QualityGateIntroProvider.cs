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
/// When a user is under a policy with a custom intro path, that intro is used
/// instead of the default Local Intros selection.
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

            var policy = QualityGateService.GetUserPolicy(user.Id);
            if (policy == null)
            {
                _logger.LogDebug("QualityGateIntroProvider: No policy for user {UserId}, using default intros", user.Id);
                return Task.FromResult(result);
            }

            var introPath = policy.IntroVideoPath;
            if (string.IsNullOrWhiteSpace(introPath))
            {
                _logger.LogDebug("QualityGateIntroProvider: Policy '{PolicyName}' has no custom intro, using default", policy.Name);
                return Task.FromResult(result);
            }

            // Check if the intro file exists
            if (!File.Exists(introPath))
            {
                _logger.LogWarning("QualityGateIntroProvider: Intro file not found: {IntroPath}", introPath);
                return Task.FromResult(result);
            }

            _logger.LogInformation(
                "QualityGateIntroProvider: User {UserName} (policy: {PolicyName}) gets custom intro: {IntroPath}",
                user.Username, policy.Name, introPath);

            // Return the custom intro - this will be added to (or replace depending on Jellyfin version)
            // the intros from other providers
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

