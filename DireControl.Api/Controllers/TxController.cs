using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace DireControl.Api.Controllers;

/// <summary>Outbound frame lifecycle (queued → on air → sent / failed).</summary>
[ApiController]
[Route("api/v0/tx")]
public class TxController(TxFrameRegistry registry) : ControllerBase
{
    /// <summary>
    /// Recent frames, newest first — what a reloaded or reconnected page
    /// resyncs from before following <c>txFrameStateChanged</c>.
    /// </summary>
    [HttpGet("recent")]
    public ActionResult<IReadOnlyList<TxFrameDto>> Recent([FromQuery] int limit = 50) =>
        Ok(registry.Recent(limit));
}
