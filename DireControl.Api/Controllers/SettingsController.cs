using DireControl.Api.Contracts;
using DireControl.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController(
    IOptions<DireControlOptions> options,
    IOptions<DirewolfOptions> direwolfOptions) : ControllerBase
{
    [HttpGet]
    public ActionResult<SettingsDto> Get()
    {
        return Ok(new SettingsDto
        {
            OurCallsign = options.Value.OurCallsign,
            StationLatitude = options.Value.StationLatitude,
            StationLongitude = options.Value.StationLongitude,
            StationExpiryTimeoutMinutes = options.Value.StationExpiryTimeoutMinutes,
            DirewolfHost = direwolfOptions.Value.Host,
            DirewolfPort = direwolfOptions.Value.Port,
            DirewolfReconnectDelaySeconds = direwolfOptions.Value.ReconnectDelaySeconds,
            CorsOrigins = options.Value.CorsOrigins,
        });
    }
}
