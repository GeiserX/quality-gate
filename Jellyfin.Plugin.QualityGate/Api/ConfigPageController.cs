using System.Net.Mime;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.QualityGate.Api;

/// <summary>
/// Controller for serving plugin configuration page assets.
/// </summary>
[ApiController]
[Route("Plugins/a1b2c3d4-e5f6-7890-abcd-ef1234567890/Configuration")]
public class ConfigPageController : ControllerBase
{
    /// <summary>
    /// Gets the configuration page JavaScript.
    /// </summary>
    /// <returns>The JavaScript file.</returns>
    [HttpGet("configPage.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetConfigPageJs()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "Jellyfin.Plugin.QualityGate.Configuration.configPage.js";
        
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return NotFound("Resource not found");
        }
        
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        
        return Content(content, "application/javascript");
    }
}

