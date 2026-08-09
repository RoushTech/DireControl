using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Enums;
using DireControl.Modem.Audio;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/modem")]
public class ModemController(
    SoundModemService modemService,
    ModemRestartTrigger restartTrigger,
    DireControlContext db) : ControllerBase
{
    /// <summary>Live status of every radio's modem instance.</summary>
    [HttpGet("status")]
    public ActionResult<List<ModemStatusDto>> GetStatus() =>
        Ok(modemService.Statuses.Select(ToDto).ToList());

    internal static ModemStatusDto ToDto(ModemStatusSnapshot s) => new()
    {
        RadioId = s.RadioId,
        RadioName = s.RadioName,
        FullCallsign = s.FullCallsign,
        Channel = s.Channel,
        State = s.State,
        CaptureDevice = s.CaptureDevice,
        ErrorMessage = s.ErrorMessage,
        AudioLevel = s.AudioLevel,
        CarrierDetected = s.CarrierDetected,
        DecodedFrames = s.DecodedFrames,
        InvalidFrames = s.InvalidFrames,
        TxEnabled = s.TxEnabled,
        Transmitting = s.Transmitting,
        TransmittedFrames = s.TransmittedFrames,
        RigFrequencyHz = s.RigFrequencyHz,
        DecodedByProfile = s.DecodedByProfile,
    };

    /// <summary>
    /// Devices available for modem configuration: ALSA PCM devices (as
    /// <c>arecord -L</c> shows), serial ports for RTS/DTR PTT, and hidraw
    /// nodes for CM108 PTT.  Each list degrades to empty when the subsystem
    /// is unavailable (e.g. container without audio devices).
    /// </summary>
    [HttpGet("devices")]
    public ActionResult<ModemDevicesDto> GetDevices()
    {
        List<ModemDeviceDto> audio;
        try
        {
            audio = AlsaDeviceEnumerator.ListPcmDevices()
                .Select(d => new ModemDeviceDto
                {
                    Name = d.Name,
                    Description = d.Description,
                    SupportsCapture = d.SupportsCapture,
                    SupportsPlayback = d.SupportsPlayback,
                })
                .ToList();
        }
        catch (Exception ex) when (ex is DllNotFoundException or AlsaException)
        {
            audio = [];
        }

        return Ok(new ModemDevicesDto
        {
            Audio = audio,
            SerialPorts = ListSerialPorts(),
            HidDevices = ListHidrawDevices(),
        });
    }

    /// <summary>
    /// Forces the modem to tear down and re-open the audio device immediately.
    /// </summary>
    [HttpPost("restart")]
    public ActionResult Restart()
    {
        restartTrigger.Trigger();
        return NoContent();
    }

    /// <summary>
    /// Adjusts a radio's TX audio level (gain) live — persists the new value and
    /// applies it to the running modem immediately, with no restart or audio gap.
    /// </summary>
    [HttpPut("{radioId}/tx-level")]
    public async Task<IActionResult> SetTxLevel(
        string radioId,
        [FromBody] SetTxLevelRequest request,
        CancellationToken ct)
    {
        if (request.TxAudioLevelPct is < 1 or > 100)
            return BadRequest("TX audio level must be between 1 and 100 percent.");

        var radio = await db.Radios.FirstOrDefaultAsync(r => r.Id == radioId, ct);
        if (radio is null)
            return NotFound();

        radio.TxAudioLevelPct = request.TxAudioLevelPct;
        await db.SaveChangesAsync(ct);

        modemService.SetTxAudioLevel(radioId, request.TxAudioLevelPct);
        return NoContent();
    }

    /// <summary>
    /// Transmits a TX calibration test tone on the radio's running modem (keys
    /// PTT, plays the tone at the current TX level, unkeys).  For setting
    /// deviation while watching the gain slider.
    /// </summary>
    [HttpPost("{radioId}/test-tone")]
    public IActionResult SendTestTone(string radioId, [FromBody] TestToneRequest request)
    {
        if (request.Kind == TestToneKind.Unknown)
            return BadRequest("Choose a test-tone kind.");

        return modemService.TryEnqueueTestTone(radioId, request.Kind, request.DurationMs)
            ? Accepted()
            : Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "No running TX-capable modem for this radio. Enable the modem and TX first.");
    }

    private static List<string> ListSerialPorts()
    {
        try
        {
            return System.IO.Ports.SerialPort.GetPortNames().Order().ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Lists /dev/hidraw* nodes with the device name from sysfs so CM108
    /// interfaces are recognisable ("C-Media Electronics Inc. USB Audio Device").
    /// </summary>
    private static List<HidDeviceDto> ListHidrawDevices()
    {
        try
        {
            return Directory.GetFiles("/dev", "hidraw*")
                .Order()
                .Select(path => new HidDeviceDto
                {
                    Path = path,
                    Name = ReadHidName(System.IO.Path.GetFileName(path)) ?? "Unknown HID device",
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string? ReadHidName(string hidrawName)
    {
        try
        {
            var uevent = $"/sys/class/hidraw/{hidrawName}/device/uevent";
            return System.IO.File.ReadLines(uevent)
                .FirstOrDefault(l => l.StartsWith("HID_NAME=", StringComparison.Ordinal))?
                ["HID_NAME=".Length..];
        }
        catch
        {
            return null;
        }
    }
}
