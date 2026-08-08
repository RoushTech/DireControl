namespace DireControl.Modem.Kiss;

/// <summary>
/// KISS framing (encoder + streaming decoder) for serving external clients —
/// the same wire protocol DireWolf/hardware TNCs speak over TCP.
/// </summary>
public static class KissCodec
{
    public const byte Fend = 0xC0;
    public const byte Fesc = 0xDB;
    public const byte Tfend = 0xDC;
    public const byte Tfesc = 0xDD;

    /// <summary>KISS command nibble 0 — a data frame.</summary>
    public const byte DataFrameCommand = 0x00;

    /// <summary>
    /// Encodes an AX.25 frame as a KISS data frame for
    /// <paramref name="channel"/> (TNC port 0–15).
    /// </summary>
    public static byte[] EncodeDataFrame(int channel, ReadOnlySpan<byte> ax25Frame)
    {
        var output = new List<byte>(ax25Frame.Length + 8) { Fend };

        AppendEscaped(output, (byte)(((channel & 0x0F) << 4) | DataFrameCommand));
        foreach (var b in ax25Frame)
            AppendEscaped(output, b);

        output.Add(Fend);
        return [.. output];
    }

    private static void AppendEscaped(List<byte> output, byte value)
    {
        switch (value)
        {
            case Fend:
                output.Add(Fesc);
                output.Add(Tfend);
                break;
            case Fesc:
                output.Add(Fesc);
                output.Add(Tfesc);
                break;
            default:
                output.Add(value);
                break;
        }
    }
}

/// <summary>
/// Streaming KISS decoder: feed it raw bytes from a client socket, and it
/// raises <see cref="FrameReceived"/> with (command, channel, payload) for
/// every complete KISS frame.  Non-data commands (TXDELAY etc.) are surfaced
/// too — callers decide what to ignore.
/// </summary>
public sealed class KissDecoder
{
    private enum State { Hunting, InFrame, InFrameEscaped }

    private State _state = State.Hunting;
    private readonly List<byte> _buffer = new(512);

    /// <summary>(command nibble, channel/port nibble, payload bytes).</summary>
    public event Action<byte, byte, byte[]>? FrameReceived;

    public void ProcessBytes(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            ProcessByte(b);
    }

    private void ProcessByte(byte b)
    {
        switch (_state)
        {
            case State.Hunting:
                if (b == KissCodec.Fend)
                {
                    _buffer.Clear();
                    _state = State.InFrame;
                }
                break;

            case State.InFrame when b == KissCodec.Fend:
                EmitFrame();
                _buffer.Clear();
                // stay in-frame: back-to-back FENDs delimit consecutive frames
                break;

            case State.InFrame when b == KissCodec.Fesc:
                _state = State.InFrameEscaped;
                break;

            case State.InFrame:
                _buffer.Add(b);
                break;

            case State.InFrameEscaped:
                _buffer.Add(b switch
                {
                    KissCodec.Tfend => KissCodec.Fend,
                    KissCodec.Tfesc => KissCodec.Fesc,
                    _ => b, // invalid escape — pass through, frame will fail downstream
                });
                _state = State.InFrame;
                break;
        }
    }

    private void EmitFrame()
    {
        if (_buffer.Count < 1)
            return; // empty frame between FENDs — keepalive noise

        var typeByte = _buffer[0];
        var command = (byte)(typeByte & 0x0F);
        var channel = (byte)((typeByte >> 4) & 0x0F);
        FrameReceived?.Invoke(command, channel, _buffer.Skip(1).ToArray());
    }
}
