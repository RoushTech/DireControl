namespace DireControl.Modem.Ax25;

/// <summary>
/// A single AX.25 address field: base callsign, SSID, and the
/// has-been-repeated (H) bit that is meaningful for digipeater entries.
/// </summary>
public readonly record struct Ax25Address(string Callsign, int Ssid, bool HasBeenRepeated = false)
{
    /// <summary>
    /// Formats as TNC2: <c>CALL</c> or <c>CALL-N</c>, with a <c>*</c> suffix
    /// when the H bit is set.
    /// </summary>
    public override string ToString()
    {
        var s = Ssid > 0 ? $"{Callsign}-{Ssid}" : Callsign;
        return HasBeenRepeated ? s + "*" : s;
    }

    /// <summary>
    /// Parses a <c>CALL</c> / <c>CALL-N</c> string (optionally with a trailing
    /// <c>*</c> marking the H bit) into its parts.  A non-numeric suffix after
    /// the dash yields SSID 0, matching historical DireControl behaviour.
    /// </summary>
    /// <summary>
    /// True when the address fits an AX.25 address field as-is: 1–6 ASCII
    /// letters or digits and an SSID of 0–15.
    /// </summary>
    public static bool IsEncodable(Ax25Address address) =>
        address.Callsign.Length is >= 1 and <= 6
        && address.Callsign.All(char.IsAsciiLetterOrDigit)
        && address.Ssid is >= 0 and <= 15;

    /// <summary>
    /// True when <paramref name="path"/> is empty (direct) or a comma-separated
    /// list of at most 8 encodable addresses — the AX.25 digipeater limit.
    /// </summary>
    public static bool IsValidPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;
        var elements = path.Split(',');
        return elements.Length <= 8 && elements.All(e =>
        {
            var parts = e.Trim().Split('-');
            return parts.Length <= 2
                && (parts.Length == 1 || (parts[1].Length is >= 1 and <= 2 && parts[1].All(char.IsAsciiDigit)))
                && IsEncodable(Parse(e.Trim()));
        });
    }

    public static Ax25Address Parse(string raw)
    {
        var hBit = raw.EndsWith('*');
        if (hBit)
            raw = raw[..^1];

        var parts = raw.Split('-', 2);
        var ssid = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        return new Ax25Address(parts[0], ssid, hBit);
    }
}
