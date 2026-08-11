namespace DireControl.Modem.Agwpe;

/// <summary>
/// One AGWPE TCP/IP API frame: a fixed 36-byte little-endian header followed
/// by <see cref="Data"/>.  Header layout (offsets): 0 port, 4 DataKind,
/// 6 PID, 8 CallFrom (10 bytes NUL-padded ASCII), 18 CallTo (10 bytes),
/// 28 data length (u32 LE), 32 user/reserved.
/// </summary>
public sealed record AgwpeFrame
{
    public byte Port { get; init; }

    /// <summary>The frame kind character ('R', 'G', 'C', 'D', 'M', …).</summary>
    public byte DataKind { get; init; }

    public byte Pid { get; init; }
    public string CallFrom { get; init; } = string.Empty;
    public string CallTo { get; init; } = string.Empty;
    public byte[] Data { get; init; } = [];

    public char Kind => (char)DataKind;

    public static AgwpeFrame Create(
        char kind, byte port = 0, string callFrom = "", string callTo = "",
        byte[]? data = null, byte pid = 0) => new()
    {
        DataKind = (byte)kind,
        Port = port,
        CallFrom = callFrom,
        CallTo = callTo,
        Data = data ?? [],
        Pid = pid,
    };
}
