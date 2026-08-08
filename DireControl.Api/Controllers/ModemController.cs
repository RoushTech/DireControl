using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using DireControl.Modem.Audio;
using Microsoft.AspNetCore.Mvc;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/modem")]
public class ModemController(
    SoundModemService modemService,
    ModemRestartTrigger restartTrigger) : ControllerBase
{
    /// <summary>Live status of the native sound modem.</summary>
    [HttpGet("status")]
    public ActionResult<ModemStatusDto> GetStatus()
    {
        var s = modemService.Status;
        return Ok(new ModemStatusDto
        {
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
        });
    }

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
