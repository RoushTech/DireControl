using System.Text;
using DireControl.Modem.Ax25;

namespace DireControl.Modem.Agwpe;

/// <summary>
/// Builds the human-readable monitor payloads AGWPE clients parse — the exact
/// header text format matters, several applications regex it.  Example:
/// <c> 1:Fm KI4ABC To APRS Via WIDE1-1 &lt;UI pid=F0 Len=25 &gt;[21:14:07]\r!info\r</c>.
/// </summary>
public static class AgwpeMonitorFormatter
{
    /// <summary>
    /// The monitor DataKind for a decoded frame: 'U' for UI, 'I' for I
    /// frames, 'S' for supervisory and other U frames.
    /// </summary>
    public static char MonitorKind(Ax25FrameType type) => type switch
    {
        Ax25FrameType.UI => 'U',
        Ax25FrameType.I => 'I',
        _ => 'S',
    };

    public static byte[] Format(Ax25Frame frame, int portDisplayNumber, DateTime localTime)
    {
        var sb = new StringBuilder(128);
        sb.Append(' ').Append(portDisplayNumber).Append(":Fm ").Append(frame.Source)
            .Append(" To ").Append(frame.Destination);

        if (frame.Path.Count > 0)
            sb.Append(" Via ").Append(string.Join(',', frame.Path.Select(p => p.ToString().TrimEnd('*'))));

        var type = frame.FrameType;
        sb.Append(" <").Append(TypeLabel(frame, type));
        if (frame.Pid is { } pid)
            sb.Append(" pid=").Append(pid.ToString("X2"));
        if (frame.Info.Length > 0)
            sb.Append(" Len=").Append(frame.Info.Length);
        sb.Append(" >[").Append(localTime.ToString("HH:mm:ss")).Append("]\r");

        var header = Encoding.ASCII.GetBytes(sb.ToString());
        if (frame.Info.Length == 0)
            return header;

        // Info field follows the header, terminated by CR.
        var payload = new byte[header.Length + frame.Info.Length + 1];
        header.CopyTo(payload, 0);
        frame.Info.CopyTo(payload, header.Length);
        payload[^1] = (byte)'\r';
        return payload;
    }

    private static string TypeLabel(Ax25Frame frame, Ax25FrameType type) => type switch
    {
        Ax25FrameType.I => $"I S{frame.Ns} R{frame.Nr}",
        Ax25FrameType.RR => $"RR R{frame.Nr}",
        Ax25FrameType.RNR => $"RNR R{frame.Nr}",
        Ax25FrameType.REJ => $"REJ R{frame.Nr}",
        Ax25FrameType.SREJ => $"SREJ R{frame.Nr}",
        Ax25FrameType.UI => "UI",
        Ax25FrameType.SABM => "C SABM",
        Ax25FrameType.SABME => "C SABME",
        Ax25FrameType.DISC => "C DISC",
        Ax25FrameType.DM => "R DM",
        Ax25FrameType.UA => "R UA",
        Ax25FrameType.FRMR => "R FRMR",
        _ => type.ToString(),
    };
}
