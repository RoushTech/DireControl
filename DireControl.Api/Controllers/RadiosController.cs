using Microsoft.Extensions.DependencyInjection;
using DireControl.Modem.Dsp;
using DireControl.Modem.Ax25;
using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/radios")]
public class RadiosController(
    DireControlContext db,
    BeaconService beaconService,
    SoundModemService modemService,
    TxFrameRegistry txFrames,
    Microsoft.Extensions.Options.IOptions<DireControlOptions> options,
    [FromKeyedServices(RestartSignals.Modem)] RestartSignal modemRestartTrigger) : ControllerBase
{
    // ─── List ──────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RadioDto>>> GetRadios(CancellationToken ct)
    {
        var radios = await db.Radios
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync(ct);

        var ids = radios.Select(r => r.Id).ToList();

        // Most recent OwnBeacon per radio
        var lastBeacons = await db.OwnBeacons
            .AsNoTracking()
            .Where(b => ids.Contains(b.RadioId))
            .GroupBy(b => b.RadioId)
            .Select(g => new
            {
                RadioId = g.Key,
                BeaconedAt = g.Max(b => b.BeaconedAt),
            })
            .ToListAsync(ct);

        // Total confirmation count per radio (via join)
        var confirmCounts = await db.DigiConfirmations
            .AsNoTracking()
            .Join(db.OwnBeacons.Where(b => ids.Contains(b.RadioId)),
                  c => c.OwnBeaconId,
                  b => b.Id,
                  (c, b) => b.RadioId)
            .GroupBy(radioId => radioId)
            .Select(g => new { RadioId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Beacon count per radio
        var beaconCounts = await db.OwnBeacons
            .AsNoTracking()
            .Where(b => ids.Contains(b.RadioId))
            .GroupBy(b => b.RadioId)
            .Select(g => new { RadioId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var lastBeaconMap = lastBeacons.ToDictionary(x => x.RadioId, x => x.BeaconedAt);
        var confirmMap = confirmCounts.ToDictionary(x => x.RadioId, x => x.Count);
        var beaconMap = beaconCounts.ToDictionary(x => x.RadioId, x => x.Count);
        var now = DateTime.UtcNow;

        return Ok(radios.Select(r => ToDto(r, lastBeaconMap, confirmMap, beaconMap, now)));
    }

    // ─── Create ────────────────────────────────────────────────────────────────

    [HttpPost]
    public async Task<ActionResult<RadioDto>> CreateRadio(
        [FromBody] CreateRadioRequest request,
        CancellationToken ct)
    {
        if (ValidateAutoBeacon(request.AutoBeaconEnabled, request.AutoBeaconIntervalSeconds) is { } beaconError)
            return BadRequest(beaconError);
        if (!Ax25Address.IsValidPath(request.BeaconPath))
            return BadRequest("Invalid beacon path. Use up to 8 comma-separated callsigns such as WIDE1-1,WIDE2-1 (1–6 characters, SSID 0–15), or leave empty for direct.");

        var radio = new Radio
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name.Trim(),
            Callsign = request.Callsign.Trim().ToUpperInvariant(),
            Ssid = string.IsNullOrWhiteSpace(request.Ssid) ? null : request.Ssid.Trim(),
            ChannelNumber = request.ChannelNumber,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            BeaconPath = string.IsNullOrWhiteSpace(request.BeaconPath) ? null : request.BeaconPath.Trim(),
            BeaconSymbol = string.IsNullOrWhiteSpace(request.BeaconSymbol) ? null : request.BeaconSymbol.Trim(),
            BeaconComment = string.IsNullOrWhiteSpace(request.BeaconComment) ? null : request.BeaconComment.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ExpectedIntervalSeconds = request.ExpectedIntervalSeconds > 0 ? request.ExpectedIntervalSeconds : 600,
            AutoBeaconEnabled = request.AutoBeaconEnabled,
            AutoBeaconIntervalSeconds = request.AutoBeaconIntervalSeconds > 0 ? request.AutoBeaconIntervalSeconds : 1800,
            FrequencyMhz = request.FrequencyMhz,
            Mode = string.IsNullOrWhiteSpace(request.Mode) ? null : request.Mode.Trim(),
        };

        radio.FullCallsign = Radio.ComputeFullCallsign(radio.Callsign, radio.Ssid);

        if (request.Modem is { } modem)
        {
            if (ValidateModemConfig(modem) is { } error)
                return BadRequest(error);
            ApplyModemConfig(radio, modem);
        }

        db.Radios.Add(radio);
        await db.SaveChangesAsync(ct);
        modemRestartTrigger.Trigger();

        var now = DateTime.UtcNow;
        return CreatedAtAction(nameof(GetRadio), new { id = radio.Id },
            ToDto(radio, [], [], [], now));
    }

    // ─── Single ────────────────────────────────────────────────────────────────

    [HttpGet("{id}")]
    public async Task<ActionResult<RadioDto>> GetRadio(string id, CancellationToken ct)
    {
        var radio = await db.Radios.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (radio is null) return NotFound();

        var lastBeacon = await db.OwnBeacons
            .AsNoTracking()
            .Where(b => b.RadioId == id)
            .OrderByDescending(b => b.BeaconedAt)
            .Select(b => (DateTime?)b.BeaconedAt)
            .FirstOrDefaultAsync(ct);

        var confirmCount = await db.DigiConfirmations
            .AsNoTracking()
            .Join(db.OwnBeacons.Where(b => b.RadioId == id),
                  c => c.OwnBeaconId,
                  b => b.Id,
                  (c, b) => c.Id)
            .CountAsync(ct);

        var beaconCount = await db.OwnBeacons.CountAsync(b => b.RadioId == id, ct);

        var now = DateTime.UtcNow;
        var lastBeaconMap = lastBeacon.HasValue
            ? new Dictionary<string, DateTime> { [id] = lastBeacon.Value }
            : new Dictionary<string, DateTime>();
        var confirmMap = new Dictionary<string, int> { [id] = confirmCount };
        var beaconMap = new Dictionary<string, int> { [id] = beaconCount };

        return Ok(ToDto(radio, lastBeaconMap, confirmMap, beaconMap, now));
    }

    // ─── Update ────────────────────────────────────────────────────────────────

    [HttpPut("{id}")]
    public async Task<ActionResult<RadioDto>> UpdateRadio(
        string id,
        [FromBody] UpdateRadioRequest request,
        CancellationToken ct)
    {
        var radio = await db.Radios.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (radio is null) return NotFound();

        if (ValidateAutoBeacon(request.AutoBeaconEnabled, request.AutoBeaconIntervalSeconds) is { } beaconError)
            return BadRequest(beaconError);
        if (!Ax25Address.IsValidPath(request.BeaconPath))
            return BadRequest("Invalid beacon path. Use up to 8 comma-separated callsigns such as WIDE1-1,WIDE2-1 (1–6 characters, SSID 0–15), or leave empty for direct.");

        radio.Name = request.Name.Trim();
        radio.Callsign = request.Callsign.Trim().ToUpperInvariant();
        radio.Ssid = string.IsNullOrWhiteSpace(request.Ssid) ? null : request.Ssid.Trim();
        radio.ChannelNumber = request.ChannelNumber;
        radio.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        radio.BeaconPath = string.IsNullOrWhiteSpace(request.BeaconPath) ? null : request.BeaconPath.Trim();
        radio.BeaconSymbol = string.IsNullOrWhiteSpace(request.BeaconSymbol) ? null : request.BeaconSymbol.Trim();
        radio.BeaconComment = string.IsNullOrWhiteSpace(request.BeaconComment) ? null : request.BeaconComment.Trim();
        radio.ExpectedIntervalSeconds = request.ExpectedIntervalSeconds > 0 ? request.ExpectedIntervalSeconds : 600;
        radio.AutoBeaconEnabled = request.AutoBeaconEnabled;
        radio.AutoBeaconIntervalSeconds = request.AutoBeaconIntervalSeconds > 0 ? request.AutoBeaconIntervalSeconds : 1800;
        radio.FrequencyMhz = request.FrequencyMhz;
        radio.Mode = string.IsNullOrWhiteSpace(request.Mode) ? null : request.Mode.Trim();
        radio.FullCallsign = Radio.ComputeFullCallsign(radio.Callsign, radio.Ssid);

        if (request.Modem is { } modem)
        {
            if (ValidateModemConfig(modem) is { } error)
                return BadRequest(error);
            ApplyModemConfig(radio, modem);
        }

        await db.SaveChangesAsync(ct);
        modemRestartTrigger.Trigger();

        var now = DateTime.UtcNow;
        var lastBeacon = await db.OwnBeacons
            .AsNoTracking()
            .Where(b => b.RadioId == id)
            .OrderByDescending(b => b.BeaconedAt)
            .Select(b => (DateTime?)b.BeaconedAt)
            .FirstOrDefaultAsync(ct);

        var lastBeaconMap = lastBeacon.HasValue
            ? new Dictionary<string, DateTime> { [id] = lastBeacon.Value }
            : new Dictionary<string, DateTime>();

        return Ok(ToDto(radio, lastBeaconMap, [], [], now));
    }

    // ─── Delete ────────────────────────────────────────────────────────────────

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteRadio(string id, CancellationToken ct)
    {
        var radio = await db.Radios.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (radio is null) return NotFound();

        db.Radios.Remove(radio);
        await db.SaveChangesAsync(ct);
        modemRestartTrigger.Trigger();
        return NoContent();
    }

    // ─── Toggle active ─────────────────────────────────────────────────────────

    [HttpPatch("{id}/active")]
    public async Task<ActionResult<RadioDto>> ToggleActive(string id, CancellationToken ct)
    {
        var radio = await db.Radios.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (radio is null) return NotFound();

        radio.IsActive = !radio.IsActive;
        await db.SaveChangesAsync(ct);
        modemRestartTrigger.Trigger();

        var now = DateTime.UtcNow;
        return Ok(ToDto(radio, [], [], [], now));
    }

    // ─── Beacon now ────────────────────────────────────────────────────────────

    [HttpPost("{id}/beacon")]
    public async Task<IActionResult> BeaconNow(string id, CancellationToken ct)
    {
        var radio = await db.Radios.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (radio is null) return NotFound();

        if (StationIdentityLogic.TransmitBlockedReason(options.Value.OurCallsign) is { } blocked)
            return Problem(statusCode: StatusCodes.Status409Conflict, detail: blocked);

        var sent = await beaconService.BeaconNowAsync(radio, ct);

        // 202: queued, not sent — the frame's lifecycle follows on txFrameStateChanged.
        return sent is null
            ? Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Beacon could not be queued: no transmitter is available (modem TX off or external TNC down), or the home position is not set.")
            : Accepted(new TxQueuedResponse { TxFrameIds = [sent.FrameId] });
    }

    // ─── PTT test ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Keys the radio's saved PTT for 1 s over silence (no RF ID, no data) and
    /// reports whether the keyup actually completed. Uses the saved PTT config
    /// — the editor asks for a save first. Blocked while the callsign is N0CALL.
    /// </summary>
    [HttpPost("{id}/ptt-test")]
    public async Task<ActionResult<PttTestResultDto>> PttTest(string id, CancellationToken ct)
    {
        if (!await db.Radios.AnyAsync(r => r.Id == id, ct))
            return NotFound();
        if (StationIdentityLogic.TransmitBlockedReason(options.Value.OurCallsign) is { } blocked)
            return Problem(statusCode: StatusCodes.Status409Conflict, detail: blocked);

        if (modemService.TryEnqueueTestTone(id, TestToneKind.PttOnly, 1000) is not { } frameId)
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "No running TX-capable modem for this radio. Enable the modem and TX, and save first.");

        var final = await txFrames.WaitForFinalAsync(frameId, TimeSpan.FromSeconds(15), ct);
        return Ok(new PttTestResultDto
        {
            TxFrameId = frameId,
            Keyed = final?.State == TxFrameState.Sent,
            Reason = final is null ? "No result within 15 s (the channel may be busy)." : final.Reason,
        });
    }

    // ─── Last beacon ───────────────────────────────────────────────────────────

    [HttpGet("{id}/lastbeacon")]
    public async Task<ActionResult<LastBeaconDto>> GetLastBeacon(string id, CancellationToken ct)
    {
        var radio = await db.Radios.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (radio is null) return NotFound();

        var beacon = await db.OwnBeacons
            .AsNoTracking()
            .Include(b => b.Confirmations)
            .Where(b => b.RadioId == id)
            .OrderByDescending(b => b.BeaconedAt)
            .FirstOrDefaultAsync(ct);

        var now = DateTime.UtcNow;
        return Ok(new LastBeaconDto
        {
            RadioId = radio.Id,
            RadioName = radio.Name,
            FullCallsign = radio.FullCallsign,
            BeaconedAt = beacon?.BeaconedAt,
            SecondsSinceBeacon = beacon is not null ? (int)(now - beacon.BeaconedAt).TotalSeconds : null,
            Latitude = beacon?.Latitude,
            Longitude = beacon?.Longitude,
            PathUsed = beacon?.PathUsed,
            Comment = beacon?.Comment,
            Heard = beacon?.Heard ?? true,
            Confirmations = beacon?.Confirmations
                .OrderBy(c => c.SecondsAfterBeacon)
                .Select(c => new DigiConfirmationDto
                {
                    Digipeater = c.DigipeaterCallsign,
                    ConfirmedAt = c.ConfirmedAt,
                    SecondsAfterBeacon = c.SecondsAfterBeacon,
                    Lat = c.DigipeaterLat,
                    Lon = c.DigipeaterLon,
                    AliasUsed = c.AliasUsed,
                })
                .ToList() ?? [],
        });
    }

    // ─── Beacon history ────────────────────────────────────────────────────────

    [HttpGet("{id}/beaconhistory")]
    public async Task<ActionResult<IReadOnlyList<OwnBeaconHistoryItemDto>>> GetBeaconHistory(
        string id,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var radio = await db.Radios.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (radio is null) return NotFound();

        var capped = Math.Clamp(limit, 1, 200);

        var beacons = await db.OwnBeacons
            .AsNoTracking()
            .Include(b => b.Confirmations)
            .Where(b => b.RadioId == id)
            .OrderByDescending(b => b.BeaconedAt)
            .Take(capped)
            .ToListAsync(ct);

        return Ok(beacons.Select(b => new OwnBeaconHistoryItemDto
        {
            Id = b.Id,
            BeaconedAt = b.BeaconedAt,
            Latitude = b.Latitude,
            Longitude = b.Longitude,
            PathUsed = b.PathUsed,
            HopCount = b.HopCount,
            Heard = b.Heard,
            Confirmations = b.Confirmations
                .OrderBy(c => c.SecondsAfterBeacon)
                .Select(c => new DigiConfirmationDto
                {
                    Digipeater = c.DigipeaterCallsign,
                    ConfirmedAt = c.ConfirmedAt,
                    SecondsAfterBeacon = c.SecondsAfterBeacon,
                    Lat = c.DigipeaterLat,
                    Lon = c.DigipeaterLon,
                    AliasUsed = c.AliasUsed,
                })
                .ToList(),
        }));
    }

    // ─── Helper ────────────────────────────────────────────────────────────────

    private static RadioDto ToDto(
        Radio radio,
        Dictionary<string, DateTime> lastBeaconMap,
        Dictionary<string, int> confirmMap,
        Dictionary<string, int> beaconMap,
        DateTime now)
    {
        lastBeaconMap.TryGetValue(radio.Id, out var lastBeaconedAt);
        return new RadioDto
        {
            Id = radio.Id,
            Name = radio.Name,
            Callsign = radio.Callsign,
            Ssid = radio.Ssid,
            FullCallsign = radio.FullCallsign,
            ChannelNumber = radio.ChannelNumber,
            Notes = radio.Notes,
            BeaconPath = radio.BeaconPath,
            BeaconSymbol = radio.BeaconSymbol,
            BeaconComment = radio.BeaconComment,
            IsActive = radio.IsActive,
            ExpectedIntervalSeconds = radio.ExpectedIntervalSeconds,
            AutoBeaconEnabled = radio.AutoBeaconEnabled,
            AutoBeaconIntervalSeconds = radio.AutoBeaconIntervalSeconds,
            LastBeaconedAt = lastBeaconMap.ContainsKey(radio.Id) ? lastBeaconedAt : null,
            SecondsSinceBeacon = lastBeaconMap.ContainsKey(radio.Id)
                ? (int)(now - lastBeaconedAt).TotalSeconds
                : null,
            ConfirmationCount = confirmMap.GetValueOrDefault(radio.Id),
            BeaconCount = beaconMap.GetValueOrDefault(radio.Id),
            FrequencyMhz = radio.FrequencyMhz,
            Mode = radio.Mode,
            Modem = new RadioModemConfigDto
            {
                ModemEnabled = radio.ModemEnabled,
                ModemCaptureDevice = radio.ModemCaptureDevice,
                ModemPlaybackDevice = radio.ModemPlaybackDevice,
                RxBitRepair = radio.RxBitRepair,
                TxEnabled = radio.TxEnabled,
                TxAudioLevelPct = radio.TxAudioLevelPct,
                TxSpaceBoostDb = radio.TxSpaceBoostDb,
                TxDelayMs = radio.TxDelayMs,
                TxTailMs = radio.TxTailMs,
                TxPersistence = radio.TxPersistence,
                TxSlotTimeMs = radio.TxSlotTimeMs,
                PttMethod = radio.PttMethod,
                PttSerialPort = radio.PttSerialPort,
                PttSerialUseRts = radio.PttSerialUseRts,
                PttSerialUseDtr = radio.PttSerialUseDtr,
                PttHidDevice = radio.PttHidDevice,
                PttHidPin = radio.PttHidPin,
                PttGpioChip = radio.PttGpioChip,
                PttGpioLine = radio.PttGpioLine,
                PttGpioActiveLow = radio.PttGpioActiveLow,
                PttRigctldHost = radio.PttRigctldHost,
                PttRigctldPort = radio.PttRigctldPort,
            },
        };
    }

    /// <summary>Returns an error message when the auto-beacon config is invalid, else null.</summary>
    private static string? ValidateAutoBeacon(bool enabled, int intervalSeconds) =>
        enabled && intervalSeconds < AutoBeaconLogic.MinIntervalSeconds
            ? $"Auto-beacon interval must be at least {AutoBeaconLogic.MinIntervalSeconds} seconds."
            : null;

    /// <summary>Returns an error message when the modem config is invalid, else null.</summary>
    private static string? ValidateModemConfig(RadioModemConfigDto modem)
    {
        if (!modem.ModemEnabled)
            return null;

        if (string.IsNullOrWhiteSpace(modem.ModemCaptureDevice))
            return "Capture device is required (e.g. \"default\").";
        if (modem.RxBitRepair is < 0 or > 2)
            return "Bit repair must be 0 (off), 1 or 2 bits.";

        if (!modem.TxEnabled)
            return null;

        if (string.IsNullOrWhiteSpace(modem.ModemPlaybackDevice))
            return "Playback device is required when TX is enabled.";
        if (modem.TxAudioLevelPct is < 1 or > 100)
            return "TX audio level must be between 1 and 100 percent.";
        if (modem.TxSpaceBoostDb is < 0 or > AfskModulator.MaxSpaceBoostDb || double.IsNaN(modem.TxSpaceBoostDb))
            return $"TX space boost must be between 0 and {AfskModulator.MaxSpaceBoostDb} dB.";
        if (modem.TxDelayMs is < 0 or > 2000)
            return "TX delay must be between 0 and 2000 ms.";
        if (modem.TxTailMs is < 0 or > 1000)
            return "TX tail must be between 0 and 1000 ms.";
        if (modem.TxPersistence is < 0 or > 255)
            return "Persistence must be between 0 and 255.";
        if (modem.TxSlotTimeMs is < 10 or > 1000)
            return "Slot time must be between 10 and 1000 ms.";

        return modem.PttMethod switch
        {
            PttMethod.SerialRtsDtr when string.IsNullOrWhiteSpace(modem.PttSerialPort) =>
                "Serial PTT requires a serial port.",
            PttMethod.SerialRtsDtr when modem is { PttSerialUseRts: false, PttSerialUseDtr: false } =>
                "Serial PTT requires at least one of RTS or DTR.",
            PttMethod.Cm108 when string.IsNullOrWhiteSpace(modem.PttHidDevice) =>
                "CM108 PTT requires a hidraw device.",
            PttMethod.Cm108 when modem.PttHidPin is < 1 or > 8 =>
                "CM108 GPIO pin must be between 1 and 8.",
            PttMethod.Rigctld when string.IsNullOrWhiteSpace(modem.PttRigctldHost) =>
                "Rigctld PTT requires a hostname.",
            PttMethod.Rigctld when modem.PttRigctldPort is < 1 or > 65535 =>
                "Rigctld port must be between 1 and 65535.",
            PttMethod.Unknown => "Choose a PTT method (or None for VOX).",
            _ => null,
        };
    }

    private static void ApplyModemConfig(Radio radio, RadioModemConfigDto modem)
    {
        radio.ModemEnabled = modem.ModemEnabled;
        radio.ModemCaptureDevice = string.IsNullOrWhiteSpace(modem.ModemCaptureDevice)
            ? "default"
            : modem.ModemCaptureDevice.Trim();
        radio.ModemPlaybackDevice = string.IsNullOrWhiteSpace(modem.ModemPlaybackDevice)
            ? "default"
            : modem.ModemPlaybackDevice.Trim();
        radio.RxBitRepair = Math.Clamp(modem.RxBitRepair, 0, 2);
        radio.TxEnabled = modem.TxEnabled;
        radio.TxAudioLevelPct = modem.TxAudioLevelPct;
        radio.TxSpaceBoostDb = modem.TxSpaceBoostDb;
        radio.TxDelayMs = modem.TxDelayMs;
        radio.TxTailMs = modem.TxTailMs;
        radio.TxPersistence = modem.TxPersistence;
        radio.TxSlotTimeMs = modem.TxSlotTimeMs;
        radio.PttMethod = modem.PttMethod;
        radio.PttSerialPort = modem.PttSerialPort?.Trim();
        radio.PttSerialUseRts = modem.PttSerialUseRts;
        radio.PttSerialUseDtr = modem.PttSerialUseDtr;
        radio.PttHidDevice = modem.PttHidDevice?.Trim();
        radio.PttHidPin = modem.PttHidPin;
        radio.PttGpioChip = modem.PttGpioChip;
        radio.PttGpioLine = modem.PttGpioLine;
        radio.PttGpioActiveLow = modem.PttGpioActiveLow;
        radio.PttRigctldHost = string.IsNullOrWhiteSpace(modem.PttRigctldHost)
            ? "localhost"
            : modem.PttRigctldHost.Trim();
        radio.PttRigctldPort = modem.PttRigctldPort;
    }
}
