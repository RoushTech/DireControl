namespace DireControl.Modem.Ax25;

/// <summary>
/// Decodes raw AX.25 UI frame bytes (as delivered by KISS or the HDLC
/// deframer, without flags or FCS) into an <see cref="Ax25Frame"/>.
/// </summary>
public static class Ax25Decoder
{
    // AX.25 UI frame layout (after KISS/HDLC framing stripped):
    //   [0..6]   Destination (TOCALL)  — 7 bytes
    //   [7..13]  Source               — 7 bytes
    //   [14..]   Repeaters            — 7 bytes each; presence determined by end-bit
    //   Control byte (0x03 for UI)
    //   PID byte  (0xF0 for APRS)
    //   Information field (rest)
    //
    // Each address byte layout (byte 6 = SSID byte):
    //   Bit 7 : H (has-been-repeated) — meaningful only for repeater entries
    //   Bits 4–1 : SSID value 0–15
    //   Bit 0 : end-of-address-list flag

    /// <summary>Minimum frame size: 7 (dest) + 7 (src) + 1 (ctrl) + 1 (pid).</summary>
    private const int MinFrameLength = 16;

    /// <summary>
    /// Attempts to decode <paramref name="data"/> as an AX.25 UI frame.
    /// Returns <see langword="false"/> when the frame is too short to contain
    /// the mandatory addresses, control, and PID bytes.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out Ax25Frame frame)
    {
        frame = null!;

        if (data.Length < MinFrameLength)
            return false;

        // Destination and source: the H bit is only meaningful on repeater
        // entries, so it is dropped here (matches TNC2 rendering, which never
        // stars the source or destination).
        var (destCall, destSsid, _, _) = DecodeAddress(data, 0);
        var (srcCall, srcSsid, _, srcEnd) = DecodeAddress(data, 7);

        var path = new List<Ax25Address>();
        var pos = 14;
        var endBit = srcEnd;

        while (!endBit && pos + 7 <= data.Length)
        {
            var (repCall, repSsid, hBit, repEnd) = DecodeAddress(data, pos);
            path.Add(new Ax25Address(repCall, repSsid, hBit));
            endBit = repEnd;
            pos += 7;
        }

        // Control and PID bytes must both be present.
        if (pos + 2 > data.Length)
            return false;

        var control = data[pos];
        var pid = data[pos + 1];
        pos += 2;

        frame = new Ax25Frame
        {
            Destination = new Ax25Address(destCall, destSsid),
            Source = new Ax25Address(srcCall, srcSsid),
            Path = path,
            Control = control,
            Pid = pid,
            Info = data[pos..].ToArray(),
        };
        return true;
    }

    private static (string Call, int Ssid, bool HBit, bool EndBit) DecodeAddress(
        ReadOnlySpan<byte> buf, int offset)
    {
        Span<char> chars = stackalloc char[6];
        for (var i = 0; i < 6; i++)
            chars[i] = (char)(buf[offset + i] >> 1);
        var call = new string(chars).TrimEnd();

        var ssidByte = buf[offset + 6];
        var ssid = (ssidByte >> 1) & 0x0F;
        var hBit = (ssidByte & 0x80) != 0;
        var endBit = (ssidByte & 0x01) != 0;
        return (call, ssid, hBit, endBit);
    }
}
