using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QualityGate;

/// <summary>
/// Hosted service that initializes the Quality Gate plugin.
/// </summary>
public class QualityGateEntryPoint : IHostedService
{
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="QualityGateEntryPoint"/> class.
    /// </summary>
    public QualityGateEntryPoint(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        ILogger<Plugin> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Set up the session manager in the Plugin instance
        Plugin.Instance?.SetupSessionManager(_sessionManager, _libraryManager, _logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
