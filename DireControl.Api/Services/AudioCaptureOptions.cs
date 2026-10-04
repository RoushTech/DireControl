namespace DireControl.Api.Services;

/// <summary>
/// Settings for off-air audio capture, used to build a corpus of real
/// transmissions that can be replayed through the demodulator offline.
/// </summary>
public class AudioCaptureOptions
{
    public const string Section = "DireControl:AudioCapture";

    // Enabling and disabling automatic capture is an operator decision and
    // lives in the database (UserSetting.AudioCaptureEnabled) so it can be
    // toggled from the UI.  Everything here is sizing and retention.

    /// <summary>Directory captures are written to, relative to the content root.</summary>
    public string Directory { get; set; } = "recordings";

    /// <summary>
    /// Seconds of audio kept in memory per radio so a transmission can be saved
    /// after the fact — a failure is only recognised once it is already over.
    /// Costs 4 bytes per sample per radio (15 s at 48 kHz is ~2.9 MB).
    /// </summary>
    public int PreBufferSeconds { get; set; } = 15;

    /// <summary>Hard cap on a single manual recording, to bound memory.</summary>
    public int MaxManualSeconds { get; set; } = 120;

    /// <summary>
    /// Minimum gap between automatic captures.  A badly misconfigured audio
    /// path fails constantly; without this it would fill the disk and allocate
    /// a snapshot buffer on the DSP thread every few seconds.
    /// </summary>
    public int MinCaptureIntervalSeconds { get; set; } = 10;

    /// <summary>Newest captures kept; older ones are deleted automatically.</summary>
    public int MaxFiles { get; set; } = 50;
}
