using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Jellyfin.Plugin.QualityGate.Configuration;
using Jellyfin.Plugin.QualityGate.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.QualityGate.Api;

/// <summary>
/// API controller for Quality Gate plugin.
/// </summary>
[ApiController]
[Route("QualityGate")]
[Authorize]
public class QualityGateController : ControllerBase
{
    /// <summary>
    /// Checks if a user can access a specific media path.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="path">The media file path.</param>
    /// <returns>Whether access is allowed.</returns>
    [HttpGet("CanAccess")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<bool> CanAccess([Required] Guid userId, [Required] string path)
    {
        return Ok(QualityGateService.CanAccessPath(userId, path));
    }

    /// <summary>
    /// Gets the policy assigned to a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>The assigned policy or null.</returns>
    [HttpGet("UserPolicy/{userId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QualityPolicy?> GetUserPolicy(Guid userId)
    {
        return Ok(QualityGateService.GetUserPolicy(userId));
    }

    /// <summary>
    /// Gets all defined policies.
    /// </summary>
    /// <returns>List of policies.</returns>
    [HttpGet("Policies")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<List<QualityPolicy>> GetPolicies()
    {
        return Ok(Plugin.Instance?.Configuration?.Policies ?? new List<QualityPolicy>());
    }

    /// <summary>
    /// Gets all user policy assignments.
    /// </summary>
    /// <returns>List of user policy assignments.</returns>
    [HttpGet("UserPolicies")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<List<UserPolicyAssignment>> GetUserPolicies()
    {
        return Ok(Plugin.Instance?.Configuration?.UserPolicies ?? new List<UserPolicyAssignment>());
    }
}


