namespace DireControl;

/// <summary>
/// Generates the APRS-IS passcode for a given callsign using the standard
/// Fletcher-16-derived algorithm used by all APRS-IS servers.
/// </summary>
public static class AprsPasscodeHelper
{
    /// <summary>
    /// Computes the APRS-IS passcode for <paramref name="callsign"/>.
    /// The passcode is derived from the base callsign only (SSID is stripped).
    /// Returns a value in the range 0–32767.
    /// </summary>
    public static int GeneratePasscode(string callsign)
    {
        var baseCallsign = callsign.ToUpperInvariant().Split('-')[0];
        var hash = 0x73e2;

        for (var i = 0; i < baseCallsign.Length; i += 2)
        {
            hash ^= baseCallsign[i] << 8;
            if (i + 1 < baseCallsign.Length)
                hash ^= baseCallsign[i + 1];
        }

        return hash & 0x7fff;
    }
}
