using System.Text.RegularExpressions;
using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
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
    private static readonly Regex PathRegex =
        new(@"^[A-Z0-9-]+(,[A-Z0-9-]+)*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [HttpGet]
    public async Task<ActionResult<SettingsDto>> Get(CancellationToken ct)
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
                .FirstOrDefaultAsync(ct);

            if (station != null)
                homePosition = new HomePositionDto { Lat = station.LastLat!.Value, Lon = station.LastLon!.Value };
        }

        var userSetting = await db.UserSettings.FindAsync([1], ct);
        var outboundPath = userSetting?.OutboundPath ?? "WIDE1-1,WIDE2-1";

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
            OutboundPath = outboundPath,
        });
    }

    [HttpPut("outbound-path")]
    public async Task<ActionResult> UpdateOutboundPath(
        [FromBody] UpdateOutboundPathRequest request,
        CancellationToken ct)
    {
        var path = request.OutboundPath?.Trim() ?? string.Empty;

        if (!string.IsNullOrEmpty(path) && !PathRegex.IsMatch(path))
            return BadRequest("Invalid path format. Use comma-separated callsigns such as WIDE1-1,WIDE2-1, or leave empty for direct.");

        var setting = await db.UserSettings.FindAsync([1], ct);
        if (setting is null)
        {
            setting = new UserSetting { Id = 1, OutboundPath = path };
            db.UserSettings.Add(setting);
        }
        else
        {
            setting.OutboundPath = path;
        }

        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
