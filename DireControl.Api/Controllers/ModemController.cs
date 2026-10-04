using System.IO.Compression;
using System.Text.Json;
using DireControl.Api.Controllers.Models;
using DireControl.Api.Logging;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Enums;
using DireControl.Modem.Audio;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/modem")]
public class ModemController(
    SoundModemService modemService,
    ModemRestartTrigger restartTrigger,
    ModemAudioCaptureService captureService,
    LogStreamBroadcaster logStream,
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

    /// <summary>
    /// Starts recording this radio's raw off-air audio at the modem's own
    /// sample rate, for replaying through the demodulator offline.  Stops
    /// automatically at the configured cap so a forgotten recording cannot grow
    /// without bound.
    /// </summary>
    [HttpPost("{radioId}/record")]
    public IActionResult StartRecording(string radioId)
    {
        if (!IsModemRunning(radioId))
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "No running modem for this radio, so there is no audio to record.");

        return captureService.StartRecording(radioId, SoundModemService.SampleRate)
            ? Accepted()
            : Conflict("A recording is already running for this radio.");
    }

    /// <summary>Stops the radio's manual recording and writes it to disk.</summary>
    [HttpDelete("{radioId}/record")]
    public IActionResult StopRecording(string radioId)
    {
        var radio = modemService.Statuses.FirstOrDefault(s => s.RadioId == radioId);
        var label = radio?.FullCallsign ?? radioId;

        return captureService.StopRecording(radioId, label, SoundModemService.SampleRate)
            ? Accepted()
            : NotFound("No recording is running for this radio.");
    }

    /// <summary>Whether failed decodes are captured automatically.</summary>
    [HttpGet("capture")]
    public ActionResult<AudioCaptureSettingsDto> GetCaptureSettings() =>
        Ok(new AudioCaptureSettingsDto
        {
            Enabled = captureService.CaptureEnabled,
            CaptureCount = captureService.List().Count,
        });

    /// <summary>Turns automatic capture of failed decodes on or off.</summary>
    [HttpPut("capture")]
    public async Task<IActionResult> SetCaptureSettings(
        [FromBody] SetAudioCaptureRequest request,
        CancellationToken ct)
    {
        await captureService.SetCaptureEnabledAsync(request.Enabled, ct);
        return NoContent();
    }

    /// <summary>
    /// Downloads every capture plus the context needed to interpret it as one
    /// zip: the audio, a manifest of modem state, and the recent log tail.
    /// Streamed straight to the response — the captures can total tens of
    /// megabytes and must not be buffered in memory first.
    /// </summary>
    [HttpGet("captures/pack")]
    public async Task<IActionResult> GetCapturePack(CancellationToken ct)
    {
        var captures = captureService.List();

        // ZipArchive is a synchronous API: it writes the central directory on
        // Dispose using Stream.Write, which Kestrel rejects by default and which
        // would otherwise truncate the archive into an unopenable file.  Sync IO
        // is enabled for this request only.
        var bodyControl = HttpContext.Features.Get<IHttpBodyControlFeature>();
        if (bodyControl is not null)
            bodyControl.AllowSynchronousIO = true;

        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition =
            $"attachment; filename=\"direcontrol-logpack-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip\"";

        using var archive = new ZipArchive(Response.Body, ZipArchiveMode.Create, leaveOpen: true);

        await WriteTextEntryAsync(archive, "README.txt", CapturePackReadme, ct);
        await WriteTextEntryAsync(
            archive,
            "manifest.json",
            JsonSerializer.Serialize(
                new
                {
                    GeneratedAtUtc = DateTime.UtcNow,
                    SampleRate = SoundModemService.SampleRate,
                    CaptureEnabled = captureService.CaptureEnabled,
                    Modems = modemService.Statuses.Select(ToDto),
                    Captures = captures,
                },
                // Matches the camelCase every other response uses; a manual
                // serializer call does not inherit the MVC options.
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                }),
            ct);

        // The log tail is what makes the audio interpretable: the channel
        // activity lines say what the modem made of each burst.
        await WriteTextEntryAsync(
            archive,
            "log.txt",
            string.Join(
                Environment.NewLine,
                logStream.Snapshot().Select(
                    e => $"{e.Timestamp:yyyy-MM-dd HH:mm:ss.fff} {e.Level,-11} {e.Category} {e.Message}"
                        + (string.IsNullOrEmpty(e.Exception) ? "" : Environment.NewLine + e.Exception))),
            ct);

        foreach (var capture in captures)
        {
            var path = captureService.ResolvePath(capture.Name);
            if (path is null)
                continue;

            var entry = archive.CreateEntry($"recordings/{capture.Name}", CompressionLevel.Fastest);
            await using var entryStream = entry.Open();
            await using var file = System.IO.File.OpenRead(path);
            await file.CopyToAsync(entryStream, ct);
        }

        return new EmptyResult();
    }

    private static async Task WriteTextEntryAsync(
        ZipArchive archive, string name, string content, CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content.AsMemory(), ct);
    }

    private const string CapturePackReadme = """
        DireControl audio log pack
        ==========================

        recordings/*.wav  Raw off-air audio at the modem's own sample rate, as the
                          demodulator heard it. Files ending "-missed.wav" were
                          captured automatically because a transmission armed
                          carrier but produced no valid frame; "-rec.wav" files
                          were recorded manually.

        manifest.json     Modem state at the time the pack was built: audio
                          levels, decoded and invalid frame counts, capture
                          devices, and per-capture metadata.

        log.txt           The most recent log entries held in memory. Lines from
                          the "DireControl.ChannelActivity" category describe each
                          carrier burst: duration, peak level, and what the
                          demodulator made of it.

        To replay the audio through the real demodulator and get a diagnostic
        report, copy recordings/*.wav into DireControl.Tests/Corpus/ and run:

            dotnet test DireControl.Tests/DireControl.Tests.csproj \
                --filter Corpus_ReplayReport

        """;

    /// <summary>
    /// Radio IDs with a manual recording in progress.  The server owns this
    /// state, so the UI recovers it after a reload instead of forgetting that a
    /// recording is still running.
    /// </summary>
    [HttpGet("recording")]
    public ActionResult<List<string>> GetRecording() =>
        Ok(modemService.Statuses
            .Select(s => s.RadioId)
            .Where(captureService.IsRecording)
            .ToList());

    /// <summary>Captured audio files on disk, newest first.</summary>
    [HttpGet("captures")]
    public ActionResult<List<AudioCaptureDto>> GetCaptures() =>
        Ok(captureService.List()
            .Select(c => new AudioCaptureDto
            {
                Name = c.Name,
                SizeBytes = c.SizeBytes,
                CapturedAtUtc = c.CapturedAtUtc,
                DurationSeconds = c.DurationSeconds,
                Reason = c.Reason,
            })
            .ToList());

    /// <summary>Downloads one capture so it can be replayed or archived.</summary>
    [HttpGet("captures/{name}")]
    public IActionResult GetCapture(string name)
    {
        var path = captureService.ResolvePath(name);
        if (path is null)
            return NotFound();

        return PhysicalFile(path, "audio/wav", name);
    }

    [HttpDelete("captures/{name}")]
    public IActionResult DeleteCapture(string name) =>
        captureService.Delete(name) ? NoContent() : NotFound();

    /// <summary>True when this radio has a running modem producing audio.</summary>
    private bool IsModemRunning(string radioId) =>
        modemService.Statuses.Any(s => s.RadioId == radioId && s.State == ModemState.Running);

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
