namespace DireControl.Data.Models;

public class SignalData
{
    public int? DecodeQuality { get; set; }
    public double? FrequencyOffsetHz { get; set; }

    /// <summary>Peak input audio level (0–1) around decode time. Native modem only.</summary>
    public double? AudioLevel { get; set; }

    /// <summary>Name of the demodulator profile that decoded the frame. Native modem only.</summary>
    public string? DemodProfile { get; set; }
}
