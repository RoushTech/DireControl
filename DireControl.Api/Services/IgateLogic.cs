namespace DireControl.Api.Services;

/// <summary>
/// Pure iGate gating rules (RF→IS and IS→RF), extracted as static functions
/// for unit testing.  Follows standard APRS iGate practice.
/// </summary>
public static class IgateLogic
{
    /// <summary>
    /// Builds the APRS-IS line for an RF-received packet, appending the
    /// <c>qAR</c> construct and gate callsign.  Returns <see langword="null"/>
    /// when the packet must not be gated:
    /// third-party traffic (<c>}</c>), general queries (<c>?</c>), or a path
    /// containing <c>TCPIP</c>/<c>TCPXX</c>/<c>NOGATE</c>/<c>RFONLY</c>.
    /// </summary>
    public static string? BuildRfToIsLine(string tnc2, string gateCallsign)
    {
        var colon = tnc2.IndexOf(':');
        var gt = tnc2.IndexOf('>');
        if (colon <= 0 || gt <= 0 || gt > colon)
            return null;

        var info = tnc2[(colon + 1)..];
        if (info.Length == 0 || info[0] is '}' or '?')
            return null;

        var header = tnc2[..colon];
        foreach (var element in header[(gt + 1)..].Split(','))
        {
            var call = element.TrimEnd('*');
            if (call.Equals("TCPIP", StringComparison.OrdinalIgnoreCase) ||
                call.Equals("TCPXX", StringComparison.OrdinalIgnoreCase) ||
                call.Equals("NOGATE", StringComparison.OrdinalIgnoreCase) ||
                call.Equals("RFONLY", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return $"{header},qAR,{gateCallsign}:{info}";
    }

    /// <summary>
    /// Builds the third-party info field for gating an APRS-IS packet to RF:
    /// <c>}SRC&gt;DEST,TCPIP,GATE*:info</c>.  Only messages are gated
    /// (standard IS→RF rule); returns <see langword="null"/> for anything
    /// else, for third-party traffic, or for malformed lines.
    /// </summary>
    public static string? BuildIsToRfThirdPartyInfo(string tnc2, string gateCallsign)
    {
        var colon = tnc2.IndexOf(':');
        var gt = tnc2.IndexOf('>');
        if (colon <= 0 || gt <= 0 || gt > colon)
            return null;

        var info = tnc2[(colon + 1)..];
        if (info.Length == 0 || info[0] != ':')
            return null; // not a message
        if (info.Length > 1 && info[1] == '}')
            return null;

        var source = tnc2[..gt];
        var headerRest = tnc2[(gt + 1)..colon];
        var destination = headerRest.Split(',')[0];

        return $"}}{source}>{destination},TCPIP,{gateCallsign}*:{info}";
    }

    /// <summary>
    /// Extracts the 9-character addressee from an APRS message info field
    /// (<c>:ADDRESSEE:body</c>), or <see langword="null"/> when the field is
    /// not a well-formed message.
    /// </summary>
    public static string? ExtractMessageAddressee(string info)
    {
        if (info.Length < 11 || info[0] != ':' || info[10] != ':')
            return null;

        var addressee = info[1..10].TrimEnd();
        return addressee.Length > 0 ? addressee : null;
    }
}
