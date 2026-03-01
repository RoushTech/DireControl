using DireControl.Api.Contracts;
using DireControl.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/status")]
public class StatusController(KissConnectionHolder connectionHolder) : ControllerBase
{
    [HttpGet]
    public ActionResult<StatusDto> GetStatus() =>
        Ok(new StatusDto { DirewolfConnected = connectionHolder.IsConnected });
}
