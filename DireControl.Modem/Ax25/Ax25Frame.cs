using System.Text;

namespace DireControl.Modem.Ax25;

/// <summary>
/// A decoded AX.25 UI frame: addresses, control/PID bytes, and the raw
/// information field.  Produced by <see cref="Ax25Decoder"/> and consumed by
/// the APRS pipeline; <see cref="ToTnc2"/> renders the canonical TNC2 string
/// used throughout DireControl.
/// </summary>
public sealed class Ax25Frame
{
    /// <summary>AX.25 control byte for an Unnumbered Information (UI) frame.</summary>
    public const byte UiControl = 0x03;

    /// <summary>AX.25 PID byte for "no layer-3 protocol" (APRS).</summary>
    public const byte NoLayer3Pid = 0xF0;

    public required Ax25Address Destination { get; init; }
    public required Ax25Address Source { get; init; }
    public IReadOnlyList<Ax25Address> Path { get; init; } = [];
    public byte Control { get; init; } = UiControl;
    public byte Pid { get; init; } = NoLayer3Pid;
    public byte[] Info { get; init; } = [];

    /// <summary>
    /// Renders the frame as a TNC2-format string —
    /// <c>SRC&gt;DEST,DIGI1,DIGI2*:info</c> — preserving the H bit on each
    /// digipeater as a <c>*</c> suffix.  The info field is decoded as ASCII to
    /// match what the rest of the pipeline stores and parses.
    /// </summary>
    public string ToTnc2()
    {
        var sb = new StringBuilder(64 + Info.Length);
        sb.Append(Source.ToString()).Append('>').Append(Destination.ToString());

        foreach (var digi in Path)
            sb.Append(',').Append(digi.ToString());

        sb.Append(':').Append(Encoding.ASCII.GetString(Info));
        return sb.ToString();
    }
}
