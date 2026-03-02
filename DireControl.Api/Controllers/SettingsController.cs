using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using DireControl.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/settings")]
public class SettingsController(
    IOptions<DireControlOptions> options,
    IOptions<DirewolfOptions> direwolfOptions,
    DireControlContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SettingsDto>> Get()
    {
        HomePositionDto? homePosition = null;

        var opt = options.Value;
        if (opt.HomeLat.HasValue && opt.HomeLon.HasValue)
        {
            homePosition = new HomePositionDto { Lat = opt.HomeLat.Value, Lon = opt.HomeLon.Value };
        }
        else
        {
            var station = await db.Stations
                .Where(s => s.Callsign == opt.OurCallsign && s.LastLat != null && s.LastLon != null)
                .Select(s => new { s.LastLat, s.LastLon })
                .FirstOrDefaultAsync();

            if (station != null)
                homePosition = new HomePositionDto { Lat = station.LastLat!.Value, Lon = station.LastLon!.Value };
        }

        return Ok(new SettingsDto
        {
            OurCallsign = opt.OurCallsign,
            HomePosition = homePosition,
            StationExpiryTimeoutMinutes = opt.StationExpiryTimeoutMinutes,
            DirewolfHost = direwolfOptions.Value.Host,
            DirewolfPort = direwolfOptions.Value.Port,
            DirewolfReconnectDelaySeconds = direwolfOptions.Value.ReconnectDelaySeconds,
            MaxRetryAttempts = opt.MaxRetryAttempts,
            InitialRetryDelaySeconds = opt.InitialRetryDelaySeconds,
        });
    }
}
