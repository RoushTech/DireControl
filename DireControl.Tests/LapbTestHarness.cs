using DireControl.Modem.Ax25;
using DireControl.Modem.Ax25.Lapb;

namespace DireControl.Tests;

/// <summary>
/// Deterministic driver for <see cref="LapbStateMachine"/> tests: records
/// every output event and lets tests report frame transmission and timer
/// expiry explicitly — no real timers anywhere.
/// </summary>
internal sealed class LapbTestHarness
{
    public LapbStateMachine Machine { get; }
    public List<LapbTxFrame> Sent { get; } = [];
    public List<byte[]> Delivered { get; } = [];
    public List<(LapbState From, LapbState To, LapbDisconnectReason? Reason)> Transitions { get; } = [];
    public List<(LapbTimer Timer, TimeSpan Duration)> TimerStarts { get; } = [];
    public List<LapbTimer> TimerStops { get; } = [];

    private int _transmitReported;

    public LapbTestHarness(LapbConfig? config = null)
    {
        // T3 off by default so idle-poll starts don't clutter assertions.
        Machine = new LapbStateMachine(config ?? new LapbConfig { T3 = TimeSpan.Zero });
        Machine.FrameToSend += f => Sent.Add(f);
        Machine.DataReceived += (_, data) => Delivered.Add(data.ToArray());
        Machine.StateChanged += (from, to, reason) => Transitions.Add((from, to, reason));
        Machine.StartTimer += (t, d) => TimerStarts.Add((t, d));
        Machine.StopTimer += t => TimerStops.Add(t);
    }

    public LapbTxFrame LastSent => Sent[^1];

    public IEnumerable<LapbTxFrame> SentOfType(Ax25FrameType type) => Sent.Where(f => f.Type == type);

    /// <summary>Reports airtime for every emitted frame not yet reported.</summary>
    public void TransmitAll()
    {
        for (; _transmitReported < Sent.Count; _transmitReported++)
            Machine.OnFrameTransmitted(Sent[_transmitReported].Token);
    }

    public void Receive(
        Ax25FrameType type,
        int ns = 0,
        int nr = 0,
        bool pf = false,
        bool command = false,
        byte[]? info = null)
    {
        Machine.OnFrameReceived(new LapbRxFrame(
            type, ns, nr, pf, command,
            Ax25ControlField.HasPid(type) ? Ax25Frame.NoLayer3Pid : null,
            info ?? []));
    }

    /// <summary>Runs an outbound SABM(E) handshake to the Connected state.</summary>
    public void EstablishOutbound()
    {
        Machine.Connect();
        TransmitAll();
        Receive(Ax25FrameType.UA, pf: true);
        ClearRecordings();
    }

    /// <summary>Accepts an inbound SABM/SABME to reach the Connected state.</summary>
    public void EstablishInbound(bool extended = false)
    {
        Machine.OnFrameReceived(new LapbRxFrame(
            extended ? Ax25FrameType.SABME : Ax25FrameType.SABM,
            0, 0, PollFinal: true, IsCommand: true, null, ReadOnlyMemory<byte>.Empty));
        Machine.AcceptIncoming(new LapbRxFrame(
            extended ? Ax25FrameType.SABME : Ax25FrameType.SABM,
            0, 0, PollFinal: true, IsCommand: true, null, ReadOnlyMemory<byte>.Empty));
        ClearRecordings();
    }

    /// <summary>Forgets recorded events (not machine state) for focused assertions.</summary>
    public void ClearRecordings()
    {
        _transmitReported = Sent.Count;
        Sent.Clear();
        _transmitReported = 0;
        Delivered.Clear();
        Transitions.Clear();
        TimerStarts.Clear();
        TimerStops.Clear();
    }

    public bool T1StartedSinceClear => TimerStarts.Any(t => t.Timer == LapbTimer.T1);
}
