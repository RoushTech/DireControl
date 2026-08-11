namespace DireControl.Modem.Ax25.Lapb;

/// <summary>
/// A received frame as the LAPB machine sees it — addresses already matched
/// and stripped by the host.  <see cref="IsCommand"/> comes from the AX.25 C
/// bits (with v1 leniency applied by the host).
/// </summary>
public readonly record struct LapbRxFrame(
    Ax25FrameType Type,
    int Ns,
    int Nr,
    bool PollFinal,
    bool IsCommand,
    byte? Pid,
    ReadOnlyMemory<byte> Info);

/// <summary>
/// A frame the LAPB machine wants transmitted.  The host encodes addresses
/// and control bytes, sends it, and reports actual airtime back via
/// <see cref="LapbStateMachine.OnFrameTransmitted"/> with <see cref="Token"/>.
/// </summary>
public sealed record LapbTxFrame(
    Ax25FrameType Type,
    int Ns,
    int Nr,
    bool PollFinal,
    bool IsCommand,
    byte? Pid,
    ReadOnlyMemory<byte> Info,
    long Token);
