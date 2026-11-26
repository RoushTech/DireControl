namespace DireControl.Api.Services;

/// <summary>
/// Pure iGate gating rules (RF→IS and IS→RF), extracted as static functions
/// for unit testing.  Follows standard APRS iGate practice.
/// </summary>
public static class IgateLogic
{
    /// <summary>
    /// Builds the APRS-IS line for a packet <em>we</em> originate directly on
    /// APRS-IS (not gated from RF): <c>CALL&gt;APRS,TCPIP*:info</c>.
    /// </summary>
    public static string BuildIsOriginatedLine(string ourCallsign, string info)
        => $"{ourCallsign.Trim().ToUpperInvariant()}>APRS,TCPIP*:{info}";

    /// <summary>
    /// Builds the APRS-IS line for an RF-received packet, appending the
    /// <c>qAR</c> construct and gate callsign.  Returns <see langword="null"/>
    /// when the packet must not be gated: general queries (<c>?</c>), a path
    /// already carrying a q construct, a path
    /// containing <c>TCPIP</c>/<c>TCPXX</c>/<c>NOGATE</c>/<c>RFONLY</c>, or an
    /// info field that is empty after truncation at CR/LF or contains NUL.
    /// Third-party packets (<c>}</c>) are unwrapped and the inner packet is
    /// gated under the same rules.
    /// </summary>
    public static string? BuildRfToIsLine(string tnc2, string gateCallsign) =>
        BuildRfToIsLine(tnc2, gateCallsign, unwrapped: false);

    private static string? BuildRfToIsLine(string tnc2, string gateCallsign, bool unwrapped)
    {
        var colon = tnc2.IndexOf(':');
        var gt = tnc2.IndexOf('>');
        if (colon <= 0 || gt <= 0 || gt > colon)
            return null;

        // aprs-is.net: the info field ends at the first CR or LF — anything
        // after it would reach the server as a separate line under our
        // verified login (packet or '#' command injection from RF).  A packet
        // that is empty once cut, or carries a NUL, is not gated at all.
        var info = tnc2[(colon + 1)..];
        var lineEnd = info.AsSpan().IndexOfAny('\r', '\n');
        if (lineEnd >= 0)
            info = info[..lineEnd];
        if (info.Length == 0 || info.Contains('\0') || info[0] == '?')
            return null;

        var header = tnc2[..colon];
        foreach (var element in header[(gt + 1)..].Split(','))
        {
            var call = element.TrimEnd('*');
            if (call.Equals("TCPIP", StringComparison.OrdinalIgnoreCase) ||
                call.Equals("TCPXX", StringComparison.OrdinalIgnoreCase) ||
                call.Equals("NOGATE", StringComparison.OrdinalIgnoreCase) ||
                call.Equals("RFONLY", StringComparison.OrdinalIgnoreCase) ||
                // A q construct means the packet has already been on APRS-IS.
                call.StartsWith("qA", StringComparison.Ordinal))
            {
                return null;
            }
        }

        // Third-party traffic: gate the inner packet, not the RF wrapper — under
        // the same rules, so anything that came from APRS-IS (TCPIP in the
        // inner path) still stays off it.  One level only.
        if (info[0] == '}')
            return unwrapped ? null : BuildRfToIsLine(info[1..], gateCallsign, unwrapped: true);

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

        // aprs-is.net IGating rules: never gate to RF a packet whose path says
        // it must stay off RF (NOGATE/RFONLY), came from an unverified client
        // (TCPXX), or was injected by an unverified igate (qAX).
        foreach (var element in headerRest.Split(','))
        {
            var call = element.TrimEnd('*');
            if (call.Equals("TCPXX", StringComparison.OrdinalIgnoreCase) ||
                call.Equals("NOGATE", StringComparison.OrdinalIgnoreCase) ||
                call.Equals("RFONLY", StringComparison.OrdinalIgnoreCase) ||
                call.Equals("qAX", StringComparison.Ordinal))
            {
                return null;
            }
        }
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

    /// <summary>The sender callsign of a TNC2 line, or null when malformed.</summary>
    public static string? ExtractSender(string tnc2)
    {
        var gt = tnc2.IndexOf('>');
        return gt > 0 ? tnc2[..gt] : null;
    }
}

/// <summary>
/// IS→RF transmit budget: at most <see cref="PerMinute"/> packets in any
/// minute and <see cref="PerFiveMinutes"/> in any five (Dire Wolf IGTXLIMIT
/// defaults), so an APRS-IS burst cannot monopolise the RF channel.
/// </summary>
public sealed class IsToRfRateLimiter(TimeProvider timeProvider)
{
    public const int PerMinute = 6;
    public const int PerFiveMinutes = 10;

    private readonly Queue<DateTimeOffset> _sent = new();
    private readonly Lock _lock = new();

    /// <summary>Claims one slot; false when either budget is spent.</summary>
    public bool TryAcquire()
    {
        lock (_lock)
        {
            var now = timeProvider.GetUtcNow();
            while (_sent.Count > 0 && now - _sent.Peek() >= TimeSpan.FromMinutes(5))
                _sent.Dequeue();

            var lastMinute = _sent.Count(t => now - t < TimeSpan.FromMinutes(1));
            if (lastMinute >= PerMinute || _sent.Count >= PerFiveMinutes)
                return false;

            _sent.Enqueue(now);
            return true;
        }
    }
}
