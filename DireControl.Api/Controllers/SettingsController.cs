using System.Text.RegularExpressions;
using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;


namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/settings")]
public class SettingsController(
    IOptions<DireControlOptions> options,
    AprsIsReconnectTrigger reconnectTrigger,
    ModemRestartTrigger modemRestartTrigger,
    KissReconnectTrigger kissReconnectTrigger,
    StationIdentityService stationIdentity,
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
            DirewolfHost = userSetting.DirewolfHost,
            DirewolfPort = userSetting.DirewolfPort,
            DirewolfReconnectDelaySeconds = userSetting.DirewolfReconnectDelaySeconds,
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
            DirewolfEnabled = userSetting.DirewolfEnabled,
            DigipeaterEnabled = userSetting.DigipeaterEnabled,
            DigipeaterMaxWideN = userSetting.DigipeaterMaxWideN,
            DigipeaterFillInOnly = userSetting.DigipeaterFillInOnly,
            KissServerEnabled = userSetting.KissServerEnabled,
            KissServerPort = userSetting.KissServerPort,
            RfToIsGatingEnabled = userSetting.RfToIsGatingEnabled,
            IsToRfGatingEnabled = userSetting.IsToRfGatingEnabled,
            IsToRfPath = userSetting.IsToRfPath,
            IsToRfRecentHeardMinutes = userSetting.IsToRfRecentHeardMinutes,
        });
    }

    [HttpPut("station")]
    public async Task<ActionResult> UpdateStationIdentity(
        [FromBody] UpdateStationIdentityRequest request,
        CancellationToken ct)
    {
        var callsign = request.Callsign?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!StationIdentityLogic.IsValidCallsign(callsign))
            return BadRequest("Invalid callsign. Use BASE or BASE-SSID, e.g. W3UWU or W3UWU-10.");

        if (request.HomeLat.HasValue != request.HomeLon.HasValue)
            return BadRequest("Latitude and longitude must be provided together.");

        if (request.HomeLat is < -90 or > 90)
            return BadRequest("Latitude must be between -90 and 90.");

        if (request.HomeLon is < -180 or > 180)
            return BadRequest("Longitude must be between -180 and 180.");

        var setting = await db.UserSettings.FindAsync([1], ct);
        if (setting is null)
        {
            setting = new UserSetting { Id = 1 };
            db.UserSettings.Add(setting);
        }

        var callsignChanged =
            !string.Equals(options.Value.OurCallsign, callsign, StringComparison.OrdinalIgnoreCase);

        setting.OurCallsign = callsign;
        setting.HomeLat = request.HomeLat;
        setting.HomeLon = request.HomeLon;

        await db.SaveChangesAsync(ct);

        stationIdentity.Apply(setting);

        // A callsign change invalidates the APRS-IS login — the passcode is
        // derived from the callsign — so force a reconnect with the new identity.
        if (callsignChanged)
            reconnectTrigger.Trigger();

        return NoContent();
    }

    [HttpPut("rf-services")]
    public async Task<ActionResult> UpdateRfServices(
        [FromBody] UpdateRfServicesRequest request,
        CancellationToken ct)
    {
        if (request.DigipeaterMaxWideN is < 1 or > 7)
            return BadRequest("Max WIDEn must be between 1 and 7.");

        if (request.KissServerPort is < 1 or > 65535)
            return BadRequest("KISS server port must be between 1 and 65535.");

        var isToRfPath = request.IsToRfPath?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(isToRfPath) && !PathRegex.IsMatch(isToRfPath))
            return BadRequest("Invalid IS→RF path. Use comma-separated callsigns such as WIDE1-1, or leave empty for direct.");

        if (request.IsToRfRecentHeardMinutes is < 1 or > 720)
            return BadRequest("IS→RF recently-heard window must be between 1 and 720 minutes.");

        var setting = await db.UserSettings.FindAsync([1], ct);
        if (setting is null)
        {
            setting = new UserSetting { Id = 1 };
            db.UserSettings.Add(setting);
        }

        setting.DigipeaterEnabled = request.DigipeaterEnabled;
        setting.DigipeaterMaxWideN = request.DigipeaterMaxWideN;
        setting.DigipeaterFillInOnly = request.DigipeaterFillInOnly;
        setting.KissServerEnabled = request.KissServerEnabled;
        setting.KissServerPort = request.KissServerPort;
        setting.RfToIsGatingEnabled = request.RfToIsGatingEnabled;
        setting.IsToRfGatingEnabled = request.IsToRfGatingEnabled;
        setting.IsToRfPath = isToRfPath;
        setting.IsToRfRecentHeardMinutes = request.IsToRfRecentHeardMinutes;

        await db.SaveChangesAsync(ct);

        // The KISS server re-reads its settings on the modem restart trigger;
        // digipeater and iGate read settings per packet.
        modemRestartTrigger.Trigger();

        return NoContent();
    }

    [HttpPut("external-tnc")]
    public async Task<ActionResult> UpdateExternalTnc(
        [FromBody] UpdateExternalTncRequest request,
        CancellationToken ct)
    {
        if (request.DirewolfEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.DirewolfHost))
                return BadRequest("External TNC hostname is required.");
            if (request.DirewolfPort is < 1 or > 65535)
                return BadRequest("External TNC port must be between 1 and 65535.");
        }

        if (request.DirewolfReconnectDelaySeconds is < 1 or > 300)
            return BadRequest("Reconnect delay must be between 1 and 300 seconds.");

        var setting = await db.UserSettings.FindAsync([1], ct);
        if (setting is null)
        {
            setting = new UserSetting { Id = 1 };
            db.UserSettings.Add(setting);
        }

        setting.DirewolfEnabled = request.DirewolfEnabled;
        setting.DirewolfHost = string.IsNullOrWhiteSpace(request.DirewolfHost)
            ? "localhost"
            : request.DirewolfHost.Trim();
        setting.DirewolfPort = request.DirewolfPort;
        setting.DirewolfReconnectDelaySeconds = request.DirewolfReconnectDelaySeconds;

        await db.SaveChangesAsync(ct);

        // Drop and re-establish (or stop) the external-TNC connection with the new settings.
        kissReconnectTrigger.Trigger();

        return NoContent();
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

    [HttpPut("weather-keys")]
    public async Task<ActionResult> UpdateWeatherApiKeys(
        [FromBody] UpdateWeatherApiKeysRequest request,
        CancellationToken ct)
    {
        var setting = await db.UserSettings.FindAsync([1], ct);
        if (setting is null)
        {
            setting = new UserSetting { Id = 1 };
            db.UserSettings.Add(setting);
        }

        setting.OpenWeatherMapApiKey = string.IsNullOrWhiteSpace(request.OpenWeatherMapApiKey)
            ? null
            : request.OpenWeatherMapApiKey.Trim();
        setting.TomorrowIoApiKey = string.IsNullOrWhiteSpace(request.TomorrowIoApiKey)
            ? null
            : request.TomorrowIoApiKey.Trim();
        setting.RadarProvider = request.RadarProvider ?? RadarProvider.IemNexrad;
        setting.RainViewerProApiKey = string.IsNullOrWhiteSpace(request.RainViewerProApiKey)
            ? null
            : request.RainViewerProApiKey.Trim();

        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
