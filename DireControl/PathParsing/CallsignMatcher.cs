using DireControl.Data.Models;

namespace DireControl.PathParsing;

/// <summary>
/// Determines whether an APRS packet source callsign matches a configured radio.
/// Handles the -0 SSID equivalence rule: "W3UWU-0" is treated as "W3UWU".
/// </summary>
public static class CallsignMatcher
{
    /// <summary>
    /// Returns true if <paramref name="packetSource"/> matches the radio's full callsign exactly,
    /// treating a missing SSID and "-0" as equivalent on both sides.
    /// </summary>
    public static bool Matches(Radio radio, string packetSource) =>
        Normalise(Radio.ComputeFullCallsign(radio.Callsign, radio.Ssid)) == Normalise(packetSource);

    public static string Normalise(string callsign)
    {
        if (string.IsNullOrEmpty(callsign))
            return callsign;

        var upper = callsign.ToUpperInvariant();

        // "-0" is equivalent to no SSID in APRS
        if (upper.EndsWith("-0"))
            return upper[..^2];

        return upper;
    }
}
