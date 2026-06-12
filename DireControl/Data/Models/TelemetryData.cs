namespace DireControl.Data.Models;

public class TelemetryData
{
    public string? SequenceNumber { get; set; }
    // Per-channel values; a null element means the channel was absent in the packet.
    public double?[]? Analogs { get; set; }
    public bool[]? Digitals { get; set; }
    public string? Comment { get; set; }
}
