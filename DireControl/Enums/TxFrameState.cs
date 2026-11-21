namespace DireControl.Enums;

/// <summary>
/// Where an outbound frame is in its life: queued → waiting for a clear
/// channel → on air → sent, or failed / dropped with a reason. Only
/// <see cref="Sent"/> means the frame actually left the radio.
/// </summary>
public enum TxFrameState
{
    Unknown = 0,
    Queued = 1,
    WaitingForChannel = 2,
    OnAir = 3,
    Sent = 4,
    Failed = 5,
    Dropped = 6,
}
