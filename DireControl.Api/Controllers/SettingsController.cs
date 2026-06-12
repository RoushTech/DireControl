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
    IOptions<DirewolfOptions> direwolfOptions,
    AprsIsReconnectTrigger reconnectTrigger,
    StationSettingsProvider settingsProvider,
    DireControlContext db) : ControllerBase
{
    private static readonly Regex PathRegex =
        new(@"^[A-Z0-9-]+(,[A-Z0-9-]+)*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Amateur callsign with optional SSID; permissive enough for portable suffixes.
    private static readonly Regex CallsignRegex =
        new(@"^[A-Z0-9]{3,9}(-([0-9]|1[0-5]))?$", RegexOptions.Compiled);

    [HttpGet]
    public async Task<ActionResult<SettingsDto>> Get(CancellationToken ct)
    {
        var effective = await settingsProvider.GetAsync(ct);

        HomePositionDto? homePosition = null;
        if (effective.HomeLat.HasValue && effective.HomeLon.HasValue)
        {
            homePosition = new HomePositionDto { Lat = effective.HomeLat.Value, Lon = effective.HomeLon.Value };
        }
        else
        {
            var station = await db.Stations
                .Where(s => s.Callsign == effective.OurCallsign && s.LastLat != null && s.LastLon != null)
                .Select(s => new { s.LastLat, s.LastLon })
                .FirstOrDefaultAsync(ct);

            if (station != null)
                homePosition = new HomePositionDto { Lat = station.LastLat!.Value, Lon = station.LastLon!.Value };
        }

        var userSetting = await db.UserSettings.FindAsync([1], ct) ?? new UserSetting { Id = 1 };
        var computedPasscode = AprsPasscodeHelper.GeneratePasscode(effective.OurCallsign);

        return Ok(new SettingsDto
        {
            OurCallsign = effective.OurCallsign,
            HomePosition = homePosition,
            HomeLat = effective.HomeLat,
            HomeLon = effective.HomeLon,
            StationExpiryTimeoutMinutes = options.Value.StationExpiryTimeoutMinutes,
            DirewolfHost = direwolfOptions.Value.Host,
            DirewolfPort = direwolfOptions.Value.Port,
            DirewolfReconnectDelaySeconds = direwolfOptions.Value.ReconnectDelaySeconds,
            MaxRetryAttempts = effective.MaxRetryAttempts,
            InitialRetryDelaySeconds = effective.InitialRetryDelaySeconds,
            DatabaseCleanupIntervalHours = effective.DatabaseCleanupIntervalHours,
            VacuumOnCleanup = effective.VacuumOnCleanup,
            QrzUsername = effective.QrzUsername,
            QrzPasswordConfigured = effective.QrzPassword is not null,
            OutboundPath = userSetting.OutboundPath,
            AprsIsEnabled = userSetting.AprsIsEnabled,
            AprsIsHost = userSetting.AprsIsHost,
            AprsIsPasscodeOverrideConfigured = userSetting.AprsIsPasscode is not null,
            AprsIsPasscodeComputed = computedPasscode,
            AprsIsFilter = userSetting.AprsIsFilter,
            DeduplicationWindowSeconds = userSetting.DeduplicationWindowSeconds,
        });
    }

    [HttpPut("station")]
    public async Task<ActionResult> UpdateStationSettings(
        [FromBody] UpdateStationSettingsRequest request,
        CancellationToken ct)
    {
        var callsign = request.OurCallsign?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!CallsignRegex.IsMatch(callsign))
            return BadRequest("Callsign must be a valid amateur callsign with optional SSID (e.g. W3UWU-10).");

        if (request.HomeLat is < -90 or > 90)
            return BadRequest("Latitude must be between -90 and 90.");
        if (request.HomeLon is < -180 or > 180)
            return BadRequest("Longitude must be between -180 and 180.");
        if ((request.HomeLat is null) != (request.HomeLon is null))
            return BadRequest("Set both latitude and longitude, or neither.");
        if (request.MaxRetryAttempts is < 1 or > 20)
            return BadRequest("Max retry attempts must be between 1 and 20.");
        if (request.InitialRetryDelaySeconds is < 5 or > 600)
            return BadRequest("Initial retry delay must be between 5 and 600 seconds.");

        var setting = await GetOrCreateRowAsync(ct);
        setting.OurCallsign = callsign;
        setting.HomeLat = request.HomeLat;
        setting.HomeLon = request.HomeLon;
        setting.MaxRetryAttempts = request.MaxRetryAttempts;
        setting.InitialRetryDelaySeconds = request.InitialRetryDelaySeconds;

        await db.SaveChangesAsync(ct);
        settingsProvider.Invalidate();

        // Callsign defines the APRS-IS login + passcode — reconnect with the new identity.
        reconnectTrigger.Trigger();

        return NoContent();
    }

    [HttpPut("qrz")]
    public async Task<ActionResult> UpdateQrzCredentials(
        [FromBody] UpdateQrzCredentialsRequest request,
        CancellationToken ct)
    {
        var setting = await GetOrCreateRowAsync(ct);

        setting.QrzUsername = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();

        // Password is a write-only secret: only change when provided or explicitly cleared.
        if (request.ClearPassword)
            setting.QrzPassword = null;
        else if (!string.IsNullOrWhiteSpace(request.Password))
            setting.QrzPassword = request.Password;

        await db.SaveChangesAsync(ct);
        settingsProvider.Invalidate();
        return NoContent();
    }

    [HttpPut("cleanup")]
    public async Task<ActionResult> UpdateCleanupSettings(
        [FromBody] UpdateCleanupSettingsRequest request,
        CancellationToken ct)
    {
        if (request.DatabaseCleanupIntervalHours is < 0 or > 720)
            return BadRequest("Cleanup interval must be between 0 (disabled) and 720 hours.");

        var setting = await GetOrCreateRowAsync(ct);
        setting.DatabaseCleanupIntervalHours = request.DatabaseCleanupIntervalHours;
        setting.VacuumOnCleanup = request.VacuumOnCleanup;

        await db.SaveChangesAsync(ct);
        settingsProvider.Invalidate();
        return NoContent();
    }

    private async Task<UserSetting> GetOrCreateRowAsync(CancellationToken ct)
    {
        var setting = await db.UserSettings.FindAsync([1], ct);
        if (setting is null)
        {
            setting = new UserSetting { Id = 1 };
            db.UserSettings.Add(setting);
        }
        return setting;
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
        // The override is a write-only secret: only change it when explicitly
        // provided or explicitly cleared.
        if (request.ClearAprsIsPasscodeOverride)
            setting.AprsIsPasscode = null;
        else if (request.AprsIsPasscodeOverride is not null)
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
