using System.Threading.Channels;
using DireControl.Api.Services.Ax25;
using DireControl.Modem.Ax25;
using DireControl.Modem.Ax25.Lapb;

namespace DireControl.Api.Services.Terminal;

/// <summary>
/// An in-process duplex pseudo-session: two <see cref="IAx25Session"/> ends
/// wired back-to-back, no RF or LAPB involved.  Used for the PMS preview —
/// the web terminal owns one end and the PMS handler serves the other,
/// exactly as it would a real inbound RF session.
/// </summary>
public sealed class LocalPipeSession : IAx25Session
{
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LocalPipeSession? _peer;
    private long _bytesIn;
    private long _bytesOut;
    private volatile bool _isClosed;

    private LocalPipeSession(
        Ax25Address local, Ax25Address remote, bool isInbound,
        Channel<byte[]> incoming, Channel<byte[]> outgoing)
    {
        Local = local;
        Remote = remote;
        IsInbound = isInbound;
        _incoming = incoming;
        _outgoing = outgoing;
    }

    /// <summary>
    /// Builds a connected pair: <c>terminalEnd</c> for the web terminal and
    /// <c>serverEnd</c> for the handler that would serve an inbound station.
    /// </summary>
    public static (LocalPipeSession TerminalEnd, LocalPipeSession ServerEnd) CreatePair(
        Ax25Address terminalCallsign, Ax25Address serverCallsign)
    {
        var toServer = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        var toTerminal = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

        var terminalEnd = new LocalPipeSession(
            terminalCallsign, serverCallsign, isInbound: false, incoming: toTerminal, outgoing: toServer);
        var serverEnd = new LocalPipeSession(
            serverCallsign, terminalCallsign, isInbound: true, incoming: toServer, outgoing: toTerminal);
        terminalEnd._peer = serverEnd;
        serverEnd._peer = terminalEnd;
        return (terminalEnd, serverEnd);
    }

    public string Id { get; } = Guid.NewGuid().ToString("n");
    public Ax25Address Local { get; }
    public Ax25Address Remote { get; }

    /// <summary>Local sessions carry no RF channel.</summary>
    public int Channel => -1;

    public IReadOnlyList<Ax25Address> Path => [];
    public bool IsInbound { get; }
    public LapbState State => _isClosed ? LapbState.Disconnected : LapbState.Connected;
    public ChannelReader<byte[]> Received => _incoming.Reader;

    public Ax25SessionStats Stats => new()
    {
        BytesIn = Interlocked.Read(ref _bytesIn),
        BytesOut = Interlocked.Read(ref _bytesOut),
    };

    public event Action<IAx25Session, LapbState, LapbState>? StateChanged;
    public event Action<IAx25Session, LapbDisconnectReason>? Closed;

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_isClosed || data.IsEmpty)
            return ValueTask.CompletedTask;
        Interlocked.Add(ref _bytesOut, data.Length);
        var peer = _peer!;
        Interlocked.Add(ref peer._bytesIn, data.Length);
        _outgoing.Writer.TryWrite(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        CloseBothEnds(LapbDisconnectReason.LocalRequest);
        return Task.CompletedTask;
    }

    public void Abort() => CloseBothEnds(LapbDisconnectReason.LocalRequest);

    private void CloseBothEnds(LapbDisconnectReason reason)
    {
        CloseThisEnd(reason);
        _peer?.CloseThisEnd(LapbDisconnectReason.RemoteDisc);
    }

    private void CloseThisEnd(LapbDisconnectReason reason)
    {
        if (_isClosed)
            return;
        _isClosed = true;
        _incoming.Writer.TryComplete();
        _outgoing.Writer.TryComplete();
        _closed.TrySetResult();
        StateChanged?.Invoke(this, LapbState.Connected, LapbState.Disconnected);
        Closed?.Invoke(this, reason);
    }
}
