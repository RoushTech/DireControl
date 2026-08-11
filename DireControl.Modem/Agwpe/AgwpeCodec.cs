using System.Buffers.Binary;
using System.Text;

namespace DireControl.Modem.Agwpe;

/// <summary>Encodes and incrementally decodes AGWPE TCP/IP API frames.</summary>
public static class AgwpeCodec
{
    public const int HeaderLength = 36;

    /// <summary>Sanity cap — no legitimate AGWPE payload approaches this.</summary>
    public const int MaxDataLength = 1024 * 1024;

    public static byte[] Encode(AgwpeFrame frame)
    {
        var bytes = new byte[HeaderLength + frame.Data.Length];
        bytes[0] = frame.Port;
        bytes[4] = frame.DataKind;
        bytes[6] = frame.Pid;
        WriteCallsign(bytes.AsSpan(8, 10), frame.CallFrom);
        WriteCallsign(bytes.AsSpan(18, 10), frame.CallTo);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), (uint)frame.Data.Length);
        frame.Data.CopyTo(bytes.AsSpan(HeaderLength));
        return bytes;
    }

    private static void WriteCallsign(Span<byte> target, string callsign)
    {
        target.Clear();
        var ascii = Encoding.ASCII.GetBytes(callsign);
        ascii.AsSpan(0, Math.Min(ascii.Length, target.Length - 1)).CopyTo(target);
    }

    private static string ReadCallsign(ReadOnlySpan<byte> source)
    {
        var end = source.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? source : source[..end]).TrimEnd();
    }

    /// <summary>
    /// Attempts to read one complete frame from the front of
    /// <paramref name="buffer"/>.  On success the consumed bytes are removed.
    /// Throws <see cref="InvalidDataException"/> on an insane length field —
    /// the caller should drop the connection (the stream is desynchronised).
    /// </summary>
    public static bool TryDecode(List<byte> buffer, out AgwpeFrame frame)
    {
        frame = null!;
        if (buffer.Count < HeaderLength)
            return false;

        Span<byte> header = stackalloc byte[HeaderLength];
        for (var i = 0; i < HeaderLength; i++)
            header[i] = buffer[i];

        var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(28, 4));
        if (dataLength > MaxDataLength)
            throw new InvalidDataException($"AGWPE frame claims {dataLength} data bytes — stream desynchronised.");

        var total = HeaderLength + (int)dataLength;
        if (buffer.Count < total)
            return false;

        var data = new byte[dataLength];
        for (var i = 0; i < dataLength; i++)
            data[i] = buffer[HeaderLength + i];

        frame = new AgwpeFrame
        {
            Port = header[0],
            DataKind = header[4],
            Pid = header[6],
            CallFrom = ReadCallsign(header.Slice(8, 10)),
            CallTo = ReadCallsign(header.Slice(18, 10)),
            Data = data,
        };

        buffer.RemoveRange(0, total);
        return true;
    }
}
