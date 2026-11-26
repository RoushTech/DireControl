using System.Threading.Channels;
using DireControl.Modem.Ax25;
using DireControl.Modem.Ax25.Lapb;

namespace DireControl.Api.Services.Ax25;

/// <summary>
/// One live LAPB session: the actor that serializes every input to its pure
/// <see cref="LapbStateMachine"/> through a single-reader mailbox — ingest
/// offers, timer fires, and API calls all post closures here, so the machine
/// itself needs no locking.
/// </summary>
internal sealed class Ax25SessionInternal : IAx25Session, IDisposable
{
    /// <summary>Send-queue segments above which <see cref="SendAsync"/> blocks.</summary>
    private const int MaxQueuedSegments = 64;

    private readonly Channel<Action> _mailbox =
        System.Threading.Channels.Channel.CreateUnbounded<Action>(
            new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<byte[]> _received =
        System.Threading.Channels.Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<LapbDisconnectReason> _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly LapbStateMachine _machine;
    private readonly LapbTimerHost _timers;
    private readonly IFrameTransmitter _transmitter;
    private readonly ILogger _logger;

    private long _bytesIn;
    private long _bytesOut;
    private volatile bool _isClosed;

    public Ax25SessionInternal(
        Ax25Address local,
        Ax25Address remote,
        IReadOnlyList<Ax25Address> path,
        int channel,
        bool isInbound,
        LapbConfig config,
        IFrameTransmitter transmitter,
        ILogger logger)
    {
        Local = local;
        Remote = remote;
        Path = path;
        Channel = channel;
        IsInbound = isInbound;
        _transmitter = transmitter;
        _logger = logger;

        _machine = new LapbStateMachine(config);
        _machine.FrameToSend += EncodeAndTransmit;
        _machine.DataReceived += (_, data) =>
        {
            Interlocked.Add(ref _bytesIn, data.Length);
            _received.Writer.TryWrite(data.ToArray());
        };
        _machine.StateChanged += OnMachineStateChanged;
        _timers = new LapbTimerHost(_machine, Post);

        _ = Task.Run(PumpMailboxAsync);
    }

    public string Id { get; } = Guid.NewGuid().ToString("n");
    public Ax25Address Local { get; }
    public Ax25Address Remote { get; }
    public int Channel { get; }
    public IReadOnlyList<Ax25Address> Path { get; }
    public bool IsInbound { get; }
    public LapbState State => _machine.State;
    public ChannelReader<byte[]> Received => _received.Reader;

    public Ax25SessionStats Stats => new()
    {
        Vs = _machine.Vs,
        Vr = _machine.Vr,
        Va = _machine.Va,
        OutstandingIFrames = _machine.OutstandingFrames,
        RetryCount = _machine.RetryCount,
        SendQueueDepth = _machine.SendQueueDepth,
        BytesIn = Interlocked.Read(ref _bytesIn),
        BytesOut = Interlocked.Read(ref _bytesOut),
    };

    public event Action<IAx25Session, LapbState, LapbState>? StateChanged;
    public event Action<IAx25Session, LapbDisconnectReason>? Closed;

    // ------------------------------------------------------------- public API

    public void StartOutbound() => Post(() => _machine.Connect());

    /// <summary>Answers an inbound SABM/SABME the manager matched to this session.</summary>
    public void AcceptIncoming(Ax25Frame sabm) =>
        Post(() => _machine.AcceptIncoming(ToRxFrame(sabm)));

    /// <summary>Feeds a received frame in; mod-128 sessions re-decode the raw bytes.</summary>
    public void OfferReceivedFrame(byte[] rawFrame, Ax25Frame decodedMod8) => Post(() =>
    {
        var frame = decodedMod8;
        if (_machine.ExtendedMode && !Ax25Decoder.TryDecode(rawFrame, out frame, extendedControl: true))
            return;
        _machine.OnFrameReceived(ToRxFrame(frame));
    });

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_isClosed || data.IsEmpty)
            return;

        // Backpressure: hold the caller while the LAPB send queue is deep.
        // (Reading the depth cross-thread is fine for a threshold check.)
        while (!_isClosed && _machine.SendQueueDepth > MaxQueuedSegments)
            await Task.Delay(25, ct);
        if (_isClosed)
            return;

        var copy = data.ToArray();
        Interlocked.Add(ref _bytesOut, copy.Length);
        Post(() => _machine.SendData(copy));
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_isClosed)
            return;
        Post(() => _machine.Disconnect());
        // The machine times the DISC out itself (T1 × N2) — this always completes.
        await _closed.Task.WaitAsync(ct);
    }

    public void Abort() => Post(() => _machine.Abort());

    // ---------------------------------------------------------------- internals

    private void Post(Action action)
    {
        if (!_mailbox.Writer.TryWrite(action) && !_isClosed)
            _logger.LogWarning("Session {Id} mailbox rejected work after close.", Id);
    }

    private async Task PumpMailboxAsync()
    {
        await foreach (var action in _mailbox.Reader.ReadAllAsync())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LAPB session {Local}→{Remote} action failed.", Local, Remote);
            }
        }
    }

    private void OnMachineStateChanged(LapbState from, LapbState to, LapbDisconnectReason? reason)
    {
        StateChanged?.Invoke(this, from, to);
        if (to == LapbState.Disconnected)
            CloseInternal(reason ?? LapbDisconnectReason.Unknown);
    }

    private void CloseInternal(LapbDisconnectReason reason)
    {
        if (_isClosed)
            return;
        _isClosed = true;
        _received.Writer.TryComplete();
        _timers.Dispose();
        _closed.TrySetResult(reason);
        Closed?.Invoke(this, reason);
        _mailbox.Writer.TryComplete();
    }

    private void EncodeAndTransmit(LapbTxFrame tx)
    {
        var extended = _machine.ExtendedMode && tx.Type
            is Ax25FrameType.I or Ax25FrameType.RR or Ax25FrameType.RNR
            or Ax25FrameType.REJ or Ax25FrameType.SREJ;
        var control = Ax25ControlField.Build(tx.Type, extended, tx.Ns, tx.Nr, tx.PollFinal);

        var frame = new Ax25Frame
        {
            Destination = new Ax25Address(Remote.Callsign, Remote.Ssid),
            Source = new Ax25Address(Local.Callsign, Local.Ssid),
            Path = Path,
            Control = control[0],
            Control2 = control.Length > 1 ? control[1] : null,
            Pid = Ax25ControlField.HasPid(tx.Type) ? (tx.Pid ?? Ax25Frame.NoLayer3Pid) : null,
            DestCommandBit = tx.IsCommand,
            SourceCommandBit = !tx.IsCommand,
            Info = tx.Info.ToArray(),
        };
        var bytes = Ax25Encoder.Encode(frame);
        var token = tx.Token;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_transmitter.TrySend(bytes, Channel, TxPriority.Session, completion, exactChannelOnly: true))
        {
            // Nothing can transmit right now: report it anyway so T1 runs and
            // drives the retry — a session must never hang on a dead radio.
            Post(() => _machine.OnFrameTransmitted(token));
            return;
        }

        // T1 starts at actual airtime; cancelled/faulted keyups still report
        // so the timers keep running (the retransmit path recovers).
        _ = completion.Task.ContinueWith(
            _ => Post(() => _machine.OnFrameTransmitted(token)),
            TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>
    /// Maps a decoded frame to the machine's view.  C-bit leniency: v1 peers
    /// send both bits equal — treat everything except an explicit v2 response
    /// (dest 0 / source 1) as a command.
    /// </summary>
    private static LapbRxFrame ToRxFrame(Ax25Frame frame) => new(
        frame.FrameType,
        frame.Ns ?? 0,
        frame.Nr ?? 0,
        frame.PollFinal,
        IsCommand: !(frame is { DestCommandBit: false, SourceCommandBit: true }),
        frame.Pid,
        frame.Info);

    public void Dispose() => CloseInternal(LapbDisconnectReason.LocalRequest);
}
