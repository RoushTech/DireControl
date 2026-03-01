namespace DireControl.Data.Models;

public class ResolvedPathEntry
{
    public string Callsign { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>
    /// True when the callsign is a real station with a known position.
    /// False for generic aliases (WIDE, RELAY, etc.) or stations not found in the Station table.
    /// </summary>
    public bool Known { get; set; }
    /// <summary>
    /// Zero-based index of this hop in the resolved path (0 = originating station).
    /// </summary>
    public int HopIndex { get; set; }
}
