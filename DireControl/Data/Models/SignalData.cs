namespace DireControl.Data.Models;

public class SignalData
{
    public int? DecodeQuality { get; set; }
    public double? FrequencyOffsetHz { get; set; }

    /// <summary>Peak input audio level (0–1) around decode time. Native modem only.</summary>
    public double? AudioLevel { get; set; }

    /// <summary>Name of the demodulator profile that decoded the frame. Native modem only.</summary>
    public string? DemodProfile { get; set; }

    /// <summary>
    /// Bit decisions the modem inverted to make this frame pass its checksum
    /// (1 or 2), or null for a frame that was valid as received.  A repaired
    /// packet is very likely right but is not guaranteed.  Native modem only.
    /// </summary>
    public int? RepairedBits { get; set; }
}
