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
    AprsIsReconnectTrigger reconnectTrigger,
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

        var userSetting = await db.UserSettings.FindAsync([1], ct) ?? new UserSetting { Id = 1 };
        var computedPasscode = AprsPasscodeHelper.GeneratePasscode(opt.OurCallsign);

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
            OutboundPath = userSetting.OutboundPath,
            AprsIsEnabled = userSetting.AprsIsEnabled,
            AprsIsHost = userSetting.AprsIsHost,
            AprsIsPort = userSetting.AprsIsPort,
            AprsIsPasscodeOverride = userSetting.AprsIsPasscode,
            AprsIsPasscodeComputed = computedPasscode,
            AprsIsFilter = userSetting.AprsIsFilter,
            DeduplicationWindowSeconds = userSetting.DeduplicationWindowSeconds,
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

    [HttpPut("aprs-is")]
    public async Task<ActionResult> UpdateAprsIsSettings(
        [FromBody] UpdateAprsIsSettingsRequest request,
        CancellationToken ct)
    {
        if (request.AprsIsPort is < 1 or > 65535)
            return BadRequest("Port must be between 1 and 65535.");

        if (string.IsNullOrWhiteSpace(request.AprsIsHost))
            return BadRequest("Server hostname is required.");

        if (request.DeduplicationWindowSeconds is < 10 or > 3600)
            return BadRequest("Deduplication window must be between 10 and 3600 seconds.");

        var setting = await db.UserSettings.FindAsync([1], ct);
        if (setting is null)
        {
            setting = new UserSetting { Id = 1 };
            db.UserSettings.Add(setting);
        }

        setting.AprsIsEnabled = request.AprsIsEnabled;
        setting.AprsIsHost = request.AprsIsHost.Trim();
        setting.AprsIsPort = request.AprsIsPort;
        setting.AprsIsPasscode = request.AprsIsPasscodeOverride;
        setting.AprsIsFilter = request.AprsIsFilter.Trim();
        setting.DeduplicationWindowSeconds = request.DeduplicationWindowSeconds;

        await db.SaveChangesAsync(ct);

        // Signal AprsIsService to drop and re-establish connection with new settings.
        reconnectTrigger.Trigger();

        return NoContent();
    }
}
