using System;
using Jellyfin.Plugin.QualityGate.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.QualityGate.Startup;

/// <summary>
/// Startup filter to register the Quality Gate middleware.
/// </summary>
public class QualityGateStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            // Add our middleware early in the pipeline
            builder.UseMiddleware<QualityGateMiddleware>();
            next(builder);
        };
    }
}

