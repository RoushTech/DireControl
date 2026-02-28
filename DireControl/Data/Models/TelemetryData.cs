namespace DireControl.Data.Models;

public class TelemetryData
{
    public string? SequenceNumber { get; set; }
    public double[]? Analogs { get; set; }
    public bool[]? Digitals { get; set; }
    public string? Comment { get; set; }
}
