namespace DireControl.Modem.Ax25.Lapb;

/// <summary>
/// AX.25 v2.x LAPB data-link state machine — pure and synchronous.  The host
/// serializes every input call (no internal locking), reacts to the output
/// events, and runs the timers the machine asks for.  No threads, no clock
/// reads: T1 deliberately starts only when the host reports actual airtime
/// via <see cref="OnFrameTransmitted"/>, so CSMA/queue delays never cause
/// spurious retransmits.
///
/// Deliberate protocol subset: no SREJ and no XID (modulo-128 is negotiated
/// solely by SABME with DM/FRMR fallback to SABM), and FRMR is never sent —
/// per v2.2 guidance, conditions v2.0 would FRMR (and received FRMRs) are
/// handled as a bounded link reset instead, avoiding FRMR ping-pong.
/// </summary>
public sealed class LapbStateMachine(LapbConfig config)
{
    private readonly LapbConfig _config = config;

    // Sequence state.
    private int _vs;              // next N(S) to assign
    private int _vr;              // next expected N(S)
    private int _va;              // oldest unacked N(S)

    // Pending outbound I-field segments not yet assigned an N(S).
    private readonly Queue<(byte Pid, byte[] Data)> _sendQueue = new();

    // Sent-but-unacked I frames by N(S), for retransmission.
    private readonly Dictionary<int, (byte Pid, byte[] Data)> _sentBuffer = [];

    // Tokens of emitted frames whose actual transmission should (re)start T1.
    private readonly HashSet<long> _t1Tokens = [];
    private long _tokenCounter;

    private int _retryCount;      // N2 counter for the current recovery/establishment
    private int _resetCount;      // bounded budget for FRMR / protocol-error link resets
    private bool _rejSent;        // suppress duplicate REJs until recovery
    private bool _ackPending;     // T2 armed, RR owed
    private bool _extendedRequested;   // SABME outstanding / accepted
    private bool _sabmFallbackDone;    // SABME→SABM downgrade used

    public LapbState State { get; private set; } = LapbState.Disconnected;

    /// <summary>Modulo-128 operation active (post-negotiation).</summary>
    public bool ExtendedMode { get; private set; }

    public bool RemoteBusy { get; private set; }
    public bool LocalBusy { get; private set; }

    public int Modulus => ExtendedMode ? 128 : 8;

    /// <summary>Segments queued but not yet assigned a sequence number.</summary>
    public int SendQueueDepth => _sendQueue.Count;

    /// <summary>I frames sent but not yet acknowledged.</summary>
    public int OutstandingFrames => (_vs - _va + Modulus) % Modulus;

    public int Vs => _vs;
    public int Vr => _vr;
    public int Va => _va;
    public int RetryCount => _retryCount;

    public event Action<LapbTxFrame>? FrameToSend;
    public event Action<byte, ReadOnlyMemory<byte>>? DataReceived;
    public event Action<LapbState, LapbState, LapbDisconnectReason?>? StateChanged;
    public event Action<LapbTimer, TimeSpan>? StartTimer;
    public event Action<LapbTimer>? StopTimer;

    /// <summary>
    /// Raised when the peer acknowledges I frames (count acked) — the host's
    /// send-backpressure hook.
    /// </summary>
    public event Action<int>? FramesAcked;

    private int WindowSize => Math.Min(_config.WindowSize, Modulus - 1);

    // ------------------------------------------------------------------ inputs

    /// <summary>Initiates an outbound connection (SABME when configured, else SABM).</summary>
    public void Connect()
    {
        if (State != LapbState.Disconnected)
            return;

        ResetLinkVariables();
        _extendedRequested = ExtendedMode = _config.RequestExtended;
        _sabmFallbackDone = false;
        _retryCount = 0;
        _resetCount = 0;
        EnterState(LapbState.AwaitingConnection, null);
        EmitConnectRequest();
    }

    /// <summary>
    /// Accepts an inbound SABM/SABME the host has already matched to this
    /// link — answers UA and enters Connected.
    /// </summary>
    public void AcceptIncoming(in LapbRxFrame sabm)
    {
        if (State != LapbState.Disconnected)
            return;
        if (sabm.Type is not (Ax25FrameType.SABM or Ax25FrameType.SABME))
            return;

        ResetLinkVariables();
        _extendedRequested = ExtendedMode = sabm.Type == Ax25FrameType.SABME;
        _retryCount = 0;
        _resetCount = 0;
        Emit(Ax25FrameType.UA, pollFinal: sabm.PollFinal, isCommand: false);
        EnterState(LapbState.Connected, null);
        RestartT3();
    }

    /// <summary>Requests a graceful disconnect (DISC).</summary>
    public void Disconnect()
    {
        switch (State)
        {
            case LapbState.Connected:
            case LapbState.TimerRecovery:
                DiscardQueues();
                _retryCount = 0;
                StopTimers();
                EnterState(LapbState.AwaitingRelease, null);
                Emit(Ax25FrameType.DISC, pollFinal: true, isCommand: true, startsT1: true);
                break;

            case LapbState.AwaitingConnection:
                StopTimers();
                EnterState(LapbState.Disconnected, LapbDisconnectReason.LocalRequest);
                break;
        }
    }

    /// <summary>Immediate teardown without a DISC exchange; emits DM.</summary>
    public void Abort()
    {
        if (State == LapbState.Disconnected)
            return;

        DiscardQueues();
        StopTimers();
        if (State is LapbState.Connected or LapbState.TimerRecovery)
            Emit(Ax25FrameType.DM, pollFinal: false, isCommand: false);
        EnterState(LapbState.Disconnected, LapbDisconnectReason.LocalRequest);
    }

    /// <summary>
    /// Queues user data, segmenting to PacLen.  Frames go out as the window
    /// allows.  Callers should watch <see cref="SendQueueDepth"/> for
    /// backpressure.
    /// </summary>
    public void SendData(ReadOnlySpan<byte> data, byte pid = Ax25Frame.NoLayer3Pid)
    {
        if (State is not (LapbState.Connected or LapbState.TimerRecovery))
            return;

        var pacLen = Math.Clamp(_config.PacLen, 16, 256);
        for (var offset = 0; offset < data.Length; offset += pacLen)
        {
            var len = Math.Min(pacLen, data.Length - offset);
            _sendQueue.Enqueue((pid, data.Slice(offset, len).ToArray()));
        }

        PumpSendQueue();
    }

    /// <summary>Drives the RNR/RR local-busy indication.</summary>
    public void SetLocalBusy(bool busy)
    {
        if (LocalBusy == busy)
            return;
        LocalBusy = busy;

        if (State is not (LapbState.Connected or LapbState.TimerRecovery))
            return;

        // Tell the peer immediately; a cleared busy condition re-opens the
        // window with a normal RR carrying the current V(R).
        Emit(busy ? Ax25FrameType.RNR : Ax25FrameType.RR, pollFinal: false, isCommand: false, nr: _vr);
        ClearAckPending();
    }

    /// <summary>The host reports that the frame with this token actually left the radio.</summary>
    public void OnFrameTransmitted(long token)
    {
        if (_t1Tokens.Remove(token) && State is not (LapbState.Disconnected))
        {
            StopTimer?.Invoke(LapbTimer.T3);
            StartTimer?.Invoke(LapbTimer.T1, _config.T1);
        }
    }

    public void OnTimerExpired(LapbTimer timer)
    {
        switch (timer)
        {
            case LapbTimer.T1:
                OnT1Expired();
                break;

            case LapbTimer.T2:
                if (_ackPending && State is LapbState.Connected or LapbState.TimerRecovery)
                {
                    _ackPending = false;
                    EmitSupervisoryStatus(pollFinal: false, isCommand: false);
                }
                break;

            case LapbTimer.T3:
                if (State == LapbState.Connected)
                {
                    // Idle link check: poll like timer recovery does.
                    _retryCount = 0;
                    EnterTimerRecovery();
                }
                break;
        }
    }

    public void OnFrameReceived(in LapbRxFrame frame)
    {
        switch (State)
        {
            case LapbState.Disconnected:
                // Sessionless responses (DM to strays) are the host's job.
                break;

            case LapbState.AwaitingConnection:
                OnFrameInAwaitingConnection(frame);
                break;

            case LapbState.Connected:
            case LapbState.TimerRecovery:
                OnFrameInConnected(frame);
                break;

            case LapbState.AwaitingRelease:
                OnFrameInAwaitingRelease(frame);
                break;
        }
    }

    // ------------------------------------------------------------- state logic

    private void OnFrameInAwaitingConnection(in LapbRxFrame frame)
    {
        switch (frame.Type)
        {
            case Ax25FrameType.UA:
                StopTimers();
                _retryCount = 0;
                ExtendedMode = _extendedRequested;
                EnterState(LapbState.Connected, null);
                RestartT3();
                PumpSendQueue();
                break;

            case Ax25FrameType.DM:
            case Ax25FrameType.FRMR:
                if (_extendedRequested && !_sabmFallbackDone)
                {
                    // Peer refused SABME — downgrade to modulo-8 once.
                    _sabmFallbackDone = true;
                    _extendedRequested = ExtendedMode = false;
                    _retryCount = 0;
                    EmitConnectRequest();
                }
                else
                {
                    StopTimers();
                    EnterState(LapbState.Disconnected, LapbDisconnectReason.RemoteDm);
                }
                break;

            case Ax25FrameType.SABM:
            case Ax25FrameType.SABME:
                // Simultaneous connect: agree on the peer's modulus and come up.
                ExtendedMode = _extendedRequested = frame.Type == Ax25FrameType.SABME;
                Emit(Ax25FrameType.UA, pollFinal: frame.PollFinal, isCommand: false);
                StopTimers();
                _retryCount = 0;
                EnterState(LapbState.Connected, null);
                RestartT3();
                break;

            case Ax25FrameType.DISC:
                Emit(Ax25FrameType.DM, pollFinal: frame.PollFinal, isCommand: false);
                break;
        }
    }

    private void OnFrameInConnected(in LapbRxFrame frame)
    {
        switch (frame.Type)
        {
            case Ax25FrameType.I:
                if (!ProcessNr(frame.Nr))
                    return;
                ProcessIFrame(frame);
                break;

            case Ax25FrameType.RR:
            case Ax25FrameType.RNR:
            case Ax25FrameType.REJ:
                ProcessSupervisory(frame);
                break;

            case Ax25FrameType.SABM:
            case Ax25FrameType.SABME:
                // Remote link reset: acknowledge and start over in the
                // requested modulus. Queued/unacked data is discarded.
                ExtendedMode = _extendedRequested = frame.Type == Ax25FrameType.SABME;
                ResetLinkVariables();
                StopTimers();
                Emit(Ax25FrameType.UA, pollFinal: frame.PollFinal, isCommand: false);
                EnterState(LapbState.Connected, LapbDisconnectReason.ProtocolError);
                RestartT3();
                break;

            case Ax25FrameType.DISC:
                DiscardQueues();
                StopTimers();
                Emit(Ax25FrameType.UA, pollFinal: frame.PollFinal, isCommand: false);
                EnterState(LapbState.Disconnected, LapbDisconnectReason.RemoteDisc);
                break;

            case Ax25FrameType.DM:
                DiscardQueues();
                StopTimers();
                EnterState(LapbState.Disconnected, LapbDisconnectReason.RemoteDm);
                break;

            case Ax25FrameType.FRMR:
            case Ax25FrameType.UA:
            case Ax25FrameType.Unknown:
                // v2.0 peers FRMR things we consider reset-worthy; unsolicited
                // UA and garbage controls are FRMR conditions for us. Either
                // way: bounded link re-establishment instead of FRMR.
                AttemptLinkReset();
                break;
        }
    }

    private void OnFrameInAwaitingRelease(in LapbRxFrame frame)
    {
        switch (frame.Type)
        {
            case Ax25FrameType.UA:
            case Ax25FrameType.DM:
                StopTimers();
                EnterState(LapbState.Disconnected, LapbDisconnectReason.LocalRequest);
                break;

            case Ax25FrameType.DISC:
                Emit(Ax25FrameType.UA, pollFinal: frame.PollFinal, isCommand: false);
                StopTimers();
                EnterState(LapbState.Disconnected, LapbDisconnectReason.RemoteDisc);
                break;

            case Ax25FrameType.I:
            case Ax25FrameType.RR:
            case Ax25FrameType.RNR:
            case Ax25FrameType.REJ:
                if (frame.IsCommand && frame.PollFinal)
                    Emit(Ax25FrameType.DM, pollFinal: true, isCommand: false);
                break;
        }
    }

    private void ProcessIFrame(in LapbRxFrame frame)
    {
        if (LocalBusy)
        {
            // Not accepting data; answer polls so the peer knows why.
            if (frame.IsCommand && frame.PollFinal)
                Emit(Ax25FrameType.RNR, pollFinal: true, isCommand: false, nr: _vr);
            return;
        }

        if (frame.Ns == _vr)
        {
            _vr = (_vr + 1) % Modulus;
            _rejSent = false;
            DataReceived?.Invoke(frame.Pid ?? Ax25Frame.NoLayer3Pid, frame.Info);

            if (frame.IsCommand && frame.PollFinal)
            {
                // Poll demands an immediate final response.
                ClearAckPending();
                EmitSupervisoryStatus(pollFinal: true, isCommand: false);
            }
            else
            {
                // Delayed ack: T2 gives an outbound I frame the chance to
                // piggyback N(R) (EmitIFrame clears the pending ack).
                _ackPending = true;
                StartTimer?.Invoke(LapbTimer.T2, _config.T2);
            }
        }
        else
        {
            // Out of sequence. One REJ per gap; polls still need a final.
            if (frame.IsCommand && frame.PollFinal)
            {
                _rejSent = true;
                Emit(Ax25FrameType.REJ, pollFinal: true, isCommand: false, nr: _vr);
            }
            else if (!_rejSent)
            {
                _rejSent = true;
                Emit(Ax25FrameType.REJ, pollFinal: false, isCommand: false, nr: _vr);
            }
        }
    }

    private void ProcessSupervisory(in LapbRxFrame frame)
    {
        RemoteBusy = frame.Type == Ax25FrameType.RNR;

        if (!ProcessNr(frame.Nr))
            return;

        var wasTimerRecovery = State == LapbState.TimerRecovery;

        if (wasTimerRecovery && !frame.IsCommand && frame.PollFinal)
        {
            // The final response we were polling for: resync V(S) to the
            // peer's N(R) and retransmit anything still outstanding.
            _retryCount = 0;
            EnterState(LapbState.Connected, null);
            if (_va != _vs)
            {
                RetransmitFrom(frame.Nr);
            }
            else
            {
                _t1Tokens.Clear();
                StopTimer?.Invoke(LapbTimer.T1);
                RestartT3();
            }
            PumpSendQueue();
        }
        else if (frame.Type == Ax25FrameType.REJ)
        {
            RetransmitFrom(frame.Nr);
            PumpSendQueue();
        }

        if (frame.IsCommand && frame.PollFinal)
        {
            // Status enquiry — answer with a final.
            ClearAckPending();
            EmitSupervisoryStatus(pollFinal: true, isCommand: false);
        }

        if (!RemoteBusy)
            PumpSendQueue();
        else if (OutstandingFrames == 0 && State == LapbState.Connected)
            StartTimer?.Invoke(LapbTimer.T1, _config.T1); // schedule a busy-poll
    }

    /// <summary>
    /// Applies an incoming N(R).  Returns false when the N(R) is invalid (a
    /// reset-worthy protocol error that has already been acted on).
    /// </summary>
    private bool ProcessNr(int nr)
    {
        // Valid range: V(A) <= N(R) <= V(S), circularly.
        var span = (_vs - _va + Modulus) % Modulus;
        var offset = (nr - _va + Modulus) % Modulus;
        if (offset > span)
        {
            AttemptLinkReset();
            return false;
        }

        if (offset == 0)
            return true;

        for (var i = 0; i < offset; i++)
            _sentBuffer.Remove((_va + i) % Modulus);
        _va = nr;
        FramesAcked?.Invoke(offset);

        if (_va == _vs)
        {
            if (State == LapbState.Connected)
            {
                // Everything acked: drop tokens of in-flight transmit reports
                // so a late OnFrameTransmitted cannot re-arm T1 needlessly.
                _t1Tokens.Clear();
                StopTimer?.Invoke(LapbTimer.T1);
                RestartT3();
            }
        }
        else
        {
            // Something is still outstanding: the ack restarts T1 for it.
            StartTimer?.Invoke(LapbTimer.T1, _config.T1);
        }

        PumpSendQueue();
        return true;
    }

    private void OnT1Expired()
    {
        switch (State)
        {
            case LapbState.AwaitingConnection:
                if (++_retryCount > _config.N2)
                {
                    StopTimers();
                    EnterState(LapbState.Disconnected, LapbDisconnectReason.RetryExhausted);
                }
                else
                {
                    EmitConnectRequest();
                }
                break;

            case LapbState.Connected:
                _retryCount = 0;
                EnterTimerRecovery();
                break;

            case LapbState.TimerRecovery:
                if (++_retryCount > _config.N2)
                {
                    DiscardQueues();
                    StopTimers();
                    Emit(Ax25FrameType.DM, pollFinal: false, isCommand: false);
                    EnterState(LapbState.Disconnected, LapbDisconnectReason.RetryExhausted);
                }
                else
                {
                    EmitPoll();
                }
                break;

            case LapbState.AwaitingRelease:
                if (++_retryCount > _config.N2)
                {
                    StopTimers();
                    EnterState(LapbState.Disconnected, LapbDisconnectReason.LocalRequest);
                }
                else
                {
                    Emit(Ax25FrameType.DISC, pollFinal: true, isCommand: true, startsT1: true);
                }
                break;
        }
    }

    private void EnterTimerRecovery()
    {
        EnterState(LapbState.TimerRecovery, null);
        _retryCount++;
        EmitPoll();
    }

    /// <summary>RR (or RNR when busy) command with P=1 — the recovery/idle poll.</summary>
    private void EmitPoll()
    {
        Emit(LocalBusy ? Ax25FrameType.RNR : Ax25FrameType.RR,
            pollFinal: true, isCommand: true, nr: _vr, startsT1: true);
    }

    private void AttemptLinkReset()
    {
        if (++_resetCount > _config.N2)
        {
            DiscardQueues();
            StopTimers();
            EnterState(LapbState.Disconnected, LapbDisconnectReason.FrmrReceived);
            return;
        }

        DiscardQueues();
        StopTimers();
        _retryCount = 0;
        ResetLinkVariables();
        _extendedRequested = ExtendedMode;
        _sabmFallbackDone = false;
        EnterState(LapbState.AwaitingConnection, LapbDisconnectReason.ProtocolError);
        EmitConnectRequest();
    }

    private void EmitConnectRequest()
    {
        Emit(_extendedRequested ? Ax25FrameType.SABME : Ax25FrameType.SABM,
            pollFinal: true, isCommand: true, startsT1: true);
    }

    // -------------------------------------------------------------- send path

    private void PumpSendQueue()
    {
        if (State != LapbState.Connected || RemoteBusy)
            return;

        while (_sendQueue.Count > 0 && OutstandingFrames < WindowSize)
        {
            var (pid, data) = _sendQueue.Dequeue();
            _sentBuffer[_vs] = (pid, data);
            EmitIFrame(_vs, pid, data);
            _vs = (_vs + 1) % Modulus;
        }
    }

    private void RetransmitFrom(int nr)
    {
        // Rewind V(S) to N(R) and resend everything from there.
        var count = (_vs - nr + Modulus) % Modulus;
        _vs = nr;
        for (var i = 0; i < count; i++)
        {
            if (!_sentBuffer.TryGetValue(_vs, out var entry))
                break;
            EmitIFrame(_vs, entry.Pid, entry.Data);
            _vs = (_vs + 1) % Modulus;
        }
    }

    private void EmitIFrame(int ns, byte pid, byte[] data)
    {
        // I frames piggyback the current V(R): any ack owed is now paid.
        ClearAckPending();
        Emit(Ax25FrameType.I, pollFinal: false, isCommand: true,
            ns: ns, nr: _vr, pid: pid, info: data, startsT1: true);
    }

    /// <summary>Sends the current receive status: RR, or RNR while busy.</summary>
    private void EmitSupervisoryStatus(bool pollFinal, bool isCommand)
    {
        Emit(LocalBusy ? Ax25FrameType.RNR : Ax25FrameType.RR,
            pollFinal: pollFinal, isCommand: isCommand, nr: _vr);
    }

    private void ClearAckPending()
    {
        if (_ackPending)
        {
            _ackPending = false;
            StopTimer?.Invoke(LapbTimer.T2);
        }
    }

    private void Emit(
        Ax25FrameType type,
        bool pollFinal,
        bool isCommand,
        int ns = 0,
        int nr = 0,
        byte? pid = null,
        byte[]? info = null,
        bool startsT1 = false)
    {
        var token = ++_tokenCounter;
        if (startsT1)
            _t1Tokens.Add(token);

        FrameToSend?.Invoke(new LapbTxFrame(
            type, ns, nr, pollFinal, isCommand, pid, info ?? [], token));
    }

    // -------------------------------------------------------------- housekeeping

    private void ResetLinkVariables()
    {
        _vs = _vr = _va = 0;
        _sentBuffer.Clear();
        _t1Tokens.Clear();
        _rejSent = false;
        _ackPending = false;
        RemoteBusy = false;
    }

    private void DiscardQueues()
    {
        _sendQueue.Clear();
        _sentBuffer.Clear();
        _t1Tokens.Clear();
    }

    private void StopTimers()
    {
        StopTimer?.Invoke(LapbTimer.T1);
        StopTimer?.Invoke(LapbTimer.T2);
        StopTimer?.Invoke(LapbTimer.T3);
        _ackPending = false;
    }

    private void RestartT3()
    {
        if (_config.T3 > TimeSpan.Zero)
            StartTimer?.Invoke(LapbTimer.T3, _config.T3);
    }

    private void EnterState(LapbState next, LapbDisconnectReason? reason)
    {
        var previous = State;
        State = next;
        if (previous != next || reason is not null)
            StateChanged?.Invoke(previous, next, reason);
    }
}
