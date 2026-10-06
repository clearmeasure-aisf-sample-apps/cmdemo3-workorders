using System.Net.Mime;
using Asp.Versioning;
using ClearMeasure.Bootcamp.Core;
using ClearMeasure.Bootcamp.Core.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClearMeasure.Bootcamp.UI.Api.Controllers;

/// <summary>
/// Read-only count of work orders per status, read from the database through the normal query path
/// (<see cref="IBus"/> → <see cref="WorkOrderCountByStatusQuery"/> → EF Core). Anonymous and a plain GET, so a
/// browser can call it with <c>fetch(url, { mode: 'no-cors' })</c> to make representative traffic. Not under the
/// API sliding-window rate limit: a dashboard sends about two requests a second from one client for a minute.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/work-orders/status-counts")]
[Route($"{ApiRoutes.VersionedApiPrefix}/work-orders/status-counts")]
public sealed class WorkOrderStatusCountsController(IBus bus) : ControllerBase
{
    /// <summary>
    /// Returns <c>{ "Draft": n, "Assigned": n, ... }</c> keyed by status key, every status present.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(typeof(Dictionary<string, int>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get()
    {
        var counts = await bus.Send(new WorkOrderCountByStatusQuery());
        return Ok(counts);
    }
}
