using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.QualityGate.Api;

/// <summary>
/// Startup filter that injects the MediaSource filter middleware into the pipeline.
/// </summary>
public class QualityGateStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Add our middleware early in the pipeline to intercept responses
            app.UseMiddleware<MediaSourceFilterMiddleware>();
            
            // Continue with the rest of the pipeline
            next(app);
        };
    }
}

