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
    PacketServicesRestartTrigger packetServicesTrigger,
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
            ConnectedModeInboundEnabled = userSetting.ConnectedModeInboundEnabled,
            ConnectedModeMaxSessions = userSetting.ConnectedModeMaxSessions,
            ConnectedModeDefaultPaclen = userSetting.ConnectedModeDefaultPaclen,
            ConnectedModeWindowSize = userSetting.ConnectedModeWindowSize,
            ConnectedModeT1Seconds = userSetting.ConnectedModeT1Seconds,
            ConnectedModeRetries = userSetting.ConnectedModeRetries,
            ConnectedModePreferMod128 = userSetting.ConnectedModePreferMod128,
            PmsEnabled = userSetting.PmsEnabled,
            PmsSsid = userSetting.PmsSsid,
            PmsBannerText = userSetting.PmsBannerText,
            PmsRetentionDays = userSetting.PmsRetentionDays,
            AgwpeServerEnabled = userSetting.AgwpeServerEnabled,
            AgwpeServerPort = userSetting.AgwpeServerPort,
            AgwpeServerBindAddress = userSetting.AgwpeServerBindAddress,
            TerminalTranscriptRetentionDays = userSetting.TerminalTranscriptRetentionDays,
            LightningAlertEnabled = userSetting.LightningAlertEnabled,
            LightningAlertRadiusKm = userSetting.LightningAlertRadiusKm,
            LightningAlertCooldownMinutes = userSetting.LightningAlertCooldownMinutes,
        });
    }

    [HttpPut("packet")]
    public async Task<ActionResult> UpdatePacketSettings(
        [FromBody] UpdatePacketSettingsRequest request,
        CancellationToken ct)
    {
        if (request.ConnectedModeMaxSessions is < 1 or > 100)
            return BadRequest("Max sessions must be between 1 and 100.");
        if (request.ConnectedModeDefaultPaclen is < 16 or > 256)
            return BadRequest("Paclen must be between 16 and 256.");
        if (request.ConnectedModeWindowSize is < 1 or > 63)
            return BadRequest("Window size must be between 1 and 63 (1–7 in modulo-8).");
        if (request.ConnectedModeT1Seconds is < 1 or > 60)
            return BadRequest("T1 must be between 1 and 60 seconds.");
        if (request.ConnectedModeRetries is < 1 or > 30)
            return BadRequest("Retry limit must be between 1 and 30.");
        if (request.PmsSsid is < 1 or > 15)
            return BadRequest("PMS SSID must be between 1 and 15.");
        if (request.PmsRetentionDays is < 0 or > 3650)
            return BadRequest("PMS retention must be between 0 and 3650 days.");
        if (request.AgwpeServerPort is < 1 or > 65535)
            return BadRequest("AGWPE port must be between 1 and 65535.");
        if (request.TerminalTranscriptRetentionDays is < 0 or > 3650)
            return BadRequest("Transcript retention must be between 0 and 3650 days.");

        var bindAddress = string.IsNullOrWhiteSpace(request.AgwpeServerBindAddress)
            ? "127.0.0.1"
            : request.AgwpeServerBindAddress.Trim();
        if (!System.Net.IPAddress.TryParse(bindAddress, out _))
            return BadRequest("AGWPE bind address must be a valid IP address (127.0.0.1 or 0.0.0.0).");

        var setting = await db.UserSettings.FindAsync([1], ct);
        if (setting is null)
        {
            setting = new UserSetting { Id = 1 };
            db.UserSettings.Add(setting);
        }

        setting.ConnectedModeInboundEnabled = request.ConnectedModeInboundEnabled;
        setting.ConnectedModeMaxSessions = request.ConnectedModeMaxSessions;
        setting.ConnectedModeDefaultPaclen = request.ConnectedModeDefaultPaclen;
        setting.ConnectedModeWindowSize = request.ConnectedModeWindowSize;
        setting.ConnectedModeT1Seconds = request.ConnectedModeT1Seconds;
        setting.ConnectedModeRetries = request.ConnectedModeRetries;
        setting.ConnectedModePreferMod128 = request.ConnectedModePreferMod128;
        setting.PmsEnabled = request.PmsEnabled;
        setting.PmsSsid = request.PmsSsid;
        setting.PmsBannerText = string.IsNullOrWhiteSpace(request.PmsBannerText)
            ? "Welcome to the DireControl mailbox. H for help."
            : request.PmsBannerText.Trim();
        setting.PmsRetentionDays = request.PmsRetentionDays;
        setting.AgwpeServerEnabled = request.AgwpeServerEnabled;
        setting.AgwpeServerPort = request.AgwpeServerPort;
        setting.AgwpeServerBindAddress = bindAddress;
        setting.TerminalTranscriptRetentionDays = request.TerminalTranscriptRetentionDays;

        await db.SaveChangesAsync(ct);

        // The PMS host and AGWPE server re-read their config on this signal.
        packetServicesTrigger.Trigger();

        return NoContent();
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
        setting.RadarProvider = request.RadarProvider ?? RadarProvider.IemNexrad;
        setting.RainViewerProApiKey = string.IsNullOrWhiteSpace(request.RainViewerProApiKey)
            ? null
            : request.RainViewerProApiKey.Trim();

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPut("lightning-alerts")]
    public async Task<ActionResult> UpdateLightningAlerts(
        [FromBody] UpdateLightningAlertsRequest request,
        CancellationToken ct)
    {
        if (request.LightningAlertRadiusKm is < 1 or > 500)
            return BadRequest("Alert radius must be between 1 and 500 km.");

        if (request.LightningAlertCooldownMinutes is < 0 or > 120)
            return BadRequest("Alert cooldown must be between 0 and 120 minutes.");

        var setting = await db.UserSettings.FindAsync([1], ct);
        if (setting is null)
        {
            setting = new UserSetting { Id = 1 };
            db.UserSettings.Add(setting);
        }

        setting.LightningAlertEnabled = request.LightningAlertEnabled;
        setting.LightningAlertRadiusKm = request.LightningAlertRadiusKm;
        setting.LightningAlertCooldownMinutes = request.LightningAlertCooldownMinutes;

        await db.SaveChangesAsync(ct);

        // LightningAlertService re-reads settings from the database every cycle.
        return NoContent();
    }
}
