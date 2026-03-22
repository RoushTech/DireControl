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

    /// <summary>
    /// The generic alias (e.g. "WIDE1", "WIDE2") that this digipeater consumed when
    /// relaying the packet, or null if the hop was a direct relay without an alias.
    /// </summary>
    public string? AliasUsed { get; set; }

    /// <summary>
    /// True when this hop is an igate that forwarded the packet between RF and APRS-IS.
    /// </summary>
    public bool IsIgate { get; set; }
}
