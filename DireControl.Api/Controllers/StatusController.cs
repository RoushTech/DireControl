using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/status")]
public class StatusController(
    KissConnectionHolder connectionHolder,
    IAprsIsStatusService aprsIsStatus) : ControllerBase
{
    [HttpGet]
    public ActionResult<StatusDto> GetStatus()
    {
        var s = aprsIsStatus;
        return Ok(new StatusDto
        {
            DirewolfConnected = connectionHolder.IsConnected,
            AprsIsState = s.State.ToString(),
            AprsIsServerName = s.ServerName,
            AprsIsFilter = s.ActiveFilter,
            AprsIsSessionPacketCount = s.SessionPacketCount,
        });
    }
}
