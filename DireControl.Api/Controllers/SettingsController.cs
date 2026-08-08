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
    ModemRestartTrigger modemRestartTrigger,
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
            DirewolfEnabled = direwolfOptions.Value.Enabled,
            ModemEnabled = userSetting.ModemEnabled,
            ModemCaptureDevice = userSetting.ModemCaptureDevice,
            ModemKissChannel = userSetting.ModemKissChannel,
            ModemTxEnabled = userSetting.ModemTxEnabled,
            ModemPlaybackDevice = userSetting.ModemPlaybackDevice,
            ModemTxAudioLevelPct = userSetting.ModemTxAudioLevelPct,
            ModemTxDelayMs = userSetting.ModemTxDelayMs,
            ModemTxTailMs = userSetting.ModemTxTailMs,
            ModemPersistence = userSetting.ModemPersistence,
            ModemSlotTimeMs = userSetting.ModemSlotTimeMs,
            ModemPttMethod = userSetting.ModemPttMethod,
            ModemPttSerialPort = userSetting.ModemPttSerialPort,
            ModemPttSerialUseRts = userSetting.ModemPttSerialUseRts,
            ModemPttSerialUseDtr = userSetting.ModemPttSerialUseDtr,
            ModemPttHidDevice = userSetting.ModemPttHidDevice,
            ModemPttHidPin = userSetting.ModemPttHidPin,
            ModemPttGpioChip = userSetting.ModemPttGpioChip,
            ModemPttGpioLine = userSetting.ModemPttGpioLine,
            ModemPttGpioActiveLow = userSetting.ModemPttGpioActiveLow,
            ModemPttRigctldHost = userSetting.ModemPttRigctldHost,
            ModemPttRigctldPort = userSetting.ModemPttRigctldPort,
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

    [HttpPut("modem")]
    public async Task<ActionResult> UpdateModemSettings(
        [FromBody] UpdateModemSettingsRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ModemCaptureDevice))
            return BadRequest("Capture device is required (e.g. \"default\").");

        if (request.ModemKissChannel is < 0 or > 15)
            return BadRequest("KISS channel must be between 0 and 15.");

        if (request.ModemTxEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.ModemPlaybackDevice))
                return BadRequest("Playback device is required when TX is enabled.");
            if (request.ModemTxAudioLevelPct is < 1 or > 100)
                return BadRequest("TX audio level must be between 1 and 100 percent.");
            if (request.ModemTxDelayMs is < 0 or > 2000)
                return BadRequest("TX delay must be between 0 and 2000 ms.");
            if (request.ModemTxTailMs is < 0 or > 1000)
                return BadRequest("TX tail must be between 0 and 1000 ms.");
            if (request.ModemPersistence is < 0 or > 255)
                return BadRequest("Persistence must be between 0 and 255.");
            if (request.ModemSlotTimeMs is < 10 or > 1000)
                return BadRequest("Slot time must be between 10 and 1000 ms.");

            switch (request.ModemPttMethod)
            {
                case PttMethod.SerialRtsDtr when string.IsNullOrWhiteSpace(request.ModemPttSerialPort):
                    return BadRequest("Serial PTT requires a serial port.");
                case PttMethod.SerialRtsDtr when request is { ModemPttSerialUseRts: false, ModemPttSerialUseDtr: false }:
                    return BadRequest("Serial PTT requires at least one of RTS or DTR.");
                case PttMethod.Cm108 when string.IsNullOrWhiteSpace(request.ModemPttHidDevice):
                    return BadRequest("CM108 PTT requires a hidraw device.");
                case PttMethod.Cm108 when request.ModemPttHidPin is < 1 or > 8:
                    return BadRequest("CM108 GPIO pin must be between 1 and 8.");
                case PttMethod.Rigctld when string.IsNullOrWhiteSpace(request.ModemPttRigctldHost):
                    return BadRequest("Rigctld PTT requires a hostname.");
                case PttMethod.Rigctld when request.ModemPttRigctldPort is < 1 or > 65535:
                    return BadRequest("Rigctld port must be between 1 and 65535.");
                case PttMethod.Unknown:
                    return BadRequest("Choose a PTT method (or None for VOX).");
            }
        }

        var setting = await db.UserSettings.FindAsync([1], ct);
        if (setting is null)
        {
            setting = new UserSetting { Id = 1 };
            db.UserSettings.Add(setting);
        }

        setting.ModemEnabled = request.ModemEnabled;
        setting.ModemCaptureDevice = request.ModemCaptureDevice.Trim();
        setting.ModemKissChannel = request.ModemKissChannel;
        setting.ModemTxEnabled = request.ModemTxEnabled;
        setting.ModemPlaybackDevice = string.IsNullOrWhiteSpace(request.ModemPlaybackDevice)
            ? "default"
            : request.ModemPlaybackDevice.Trim();
        setting.ModemTxAudioLevelPct = request.ModemTxAudioLevelPct;
        setting.ModemTxDelayMs = request.ModemTxDelayMs;
        setting.ModemTxTailMs = request.ModemTxTailMs;
        setting.ModemPersistence = request.ModemPersistence;
        setting.ModemSlotTimeMs = request.ModemSlotTimeMs;
        setting.ModemPttMethod = request.ModemPttMethod;
        setting.ModemPttSerialPort = request.ModemPttSerialPort?.Trim();
        setting.ModemPttSerialUseRts = request.ModemPttSerialUseRts;
        setting.ModemPttSerialUseDtr = request.ModemPttSerialUseDtr;
        setting.ModemPttHidDevice = request.ModemPttHidDevice?.Trim();
        setting.ModemPttHidPin = request.ModemPttHidPin;
        setting.ModemPttGpioChip = request.ModemPttGpioChip;
        setting.ModemPttGpioLine = request.ModemPttGpioLine;
        setting.ModemPttGpioActiveLow = request.ModemPttGpioActiveLow;
        setting.ModemPttRigctldHost = request.ModemPttRigctldHost.Trim();
        setting.ModemPttRigctldPort = request.ModemPttRigctldPort;

        await db.SaveChangesAsync(ct);

        // Signal SoundModemService to restart with the new settings.
        modemRestartTrigger.Trigger();

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
