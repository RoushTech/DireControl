using System.Threading.Channels;
using DireControl.Modem.Ax25;
using DireControl.Modem.Ax25.Lapb;

namespace DireControl.Api.Services.Ax25;

/// <summary>Point-in-time counters for one LAPB session.</summary>
public sealed record Ax25SessionStats
{
    public int Vs { get; init; }
    public int Vr { get; init; }
    public int Va { get; init; }
    public int OutstandingIFrames { get; init; }
    public int RetryCount { get; init; }
    public int SendQueueDepth { get; init; }
    public long BytesIn { get; init; }
    public long BytesOut { get; init; }
}

/// <summary>
/// One connected-mode AX.25 (LAPB) session — the boundary the terminal, PMS,
/// and AGWPE layers consume.  Obtained from
/// <see cref="Ax25SessionManager.ConnectAsync"/> (outbound) or handed to an
/// <see cref="IAx25InboundHandler"/> (inbound).
/// </summary>
public interface IAx25Session
{
    /// <summary>Stable id for UI/SignalR correlation.</summary>
    string Id { get; }

    Ax25Address Local { get; }
    Ax25Address Remote { get; }

    /// <summary>The radio's KISS channel number this session is bound to.</summary>
    int Channel { get; }

    /// <summary>Digipeater path used for this session's frames (H bits clear).</summary>
    IReadOnlyList<Ax25Address> Path { get; }

    bool IsInbound { get; }
    LapbState State { get; }
    Ax25SessionStats Stats { get; }

    /// <summary>
    /// Queues user data on the link.  Awaits when the send buffer is full
    /// (backpressure); completes without sending when the session has closed.
    /// </summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// In-order received payloads.  Completes when the session closes.
    /// The session's single owner is the intended (sole) reader.
    /// </summary>
    ChannelReader<byte[]> Received { get; }

    event Action<IAx25Session, LapbState, LapbState>? StateChanged;

    /// <summary>Raised exactly once, when the session reaches Disconnected for good.</summary>
    event Action<IAx25Session, LapbDisconnectReason>? Closed;

    /// <summary>Graceful DISC; falls back to abort when the peer never answers.</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>Immediate teardown (DM, no DISC exchange).</summary>
    void Abort();
}

/// <summary>
/// Serves inbound sessions for a listener callsign registered via
/// <see cref="Ax25SessionManager.RegisterListener"/>.  The handler owns the
/// session — reading <see cref="IAx25Session.Received"/> and calling
/// <see cref="IAx25Session.SendAsync"/> — until <see cref="IAx25Session.Closed"/>.
/// </summary>
public interface IAx25InboundHandler
{
    Task HandleSessionAsync(IAx25Session session, CancellationToken ct);
}
