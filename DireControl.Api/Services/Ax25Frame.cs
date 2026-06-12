using System.Text;

namespace DireControl.Api.Services;

/// <summary>
/// Encodes raw AX.25 UI frames suitable for passing to
/// <see cref="AprsSharp.KissTnc.Tnc.SendData"/>.
/// </summary>
public static class Ax25Frame
{
    /// <param name="path">
    /// Comma-separated digipeater path (e.g. "WIDE1-1,WIDE2-1").
    /// Pass an empty string for direct/no-path operation.
    /// </param>
    public static byte[] BuildUiFrame(string sourceCallsign, string aprsInfo, string path)
    {
        // APRS destination — "APRS" is the standard tocall for generic APRS.
        const string destination = "APRS";

        var (destBase, destSsid) = SplitCallsign(destination);
        var (srcBase, srcSsid) = SplitCallsign(sourceCallsign);

        var pathItems = string.IsNullOrWhiteSpace(path)
            ? []
            : path.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var frame = new List<byte>(128);

        // Destination is never the last address; source is last only with no path.
        frame.AddRange(EncodeAddress(destBase, destSsid, isLast: false));
        frame.AddRange(EncodeAddress(srcBase, srcSsid, isLast: pathItems.Length == 0));

        for (var i = 0; i < pathItems.Length; i++)
        {
            var (dBase, dSsid) = SplitCallsign(pathItems[i]);
            frame.AddRange(EncodeAddress(dBase, dSsid, isLast: i == pathItems.Length - 1));
        }

        frame.Add(0x03); // Control: Unnumbered Information (UI)
        frame.Add(0xF0); // PID: no layer-3 protocol

        frame.AddRange(Encoding.ASCII.GetBytes(aprsInfo));

        return [.. frame];
    }

    /// <summary>
    /// Encodes a single AX.25 address field (7 bytes).
    /// </summary>
    private static byte[] EncodeAddress(string callsign, int ssid, bool isLast)
    {
        var padded = callsign.ToUpperInvariant().PadRight(6)[..6];

        var bytes = new byte[7];
        for (var i = 0; i < 6; i++)
            bytes[i] = (byte)((padded[i] & 0x7F) << 1);

        // SSID byte: bits 7-6 = 1 (H/C reserved), bits 4-1 = SSID, bit 0 = end
        bytes[6] = (byte)(0x60 | ((ssid & 0x0F) << 1) | (isLast ? 0x01 : 0x00));

        return bytes;
    }

    private static (string callsign, int ssid) SplitCallsign(string raw)
    {
        var parts = raw.Split('-', 2);
        var ssid = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        return (parts[0], ssid);
    }
}
