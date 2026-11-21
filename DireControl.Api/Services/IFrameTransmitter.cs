namespace DireControl.Api.Services;

/// <summary>Transmit urgency for an outbound AX.25 frame.</summary>
public enum TxPriority
{
    /// <summary>Beacons, digipeats, APRS messages — batched, order-tolerant.</summary>
    Normal = 0,

    /// <summary>
    /// Connected-mode (LAPB) session frames — drained ahead of normal traffic
    /// so acks and retransmits are not stuck behind a beacon backlog.
    /// </summary>
    Session,

    /// <summary>
    /// Digipeats — drained ahead of beacons and other normal traffic, since a
    /// late digipeat behaves like a duplicate on the network.
    /// </summary>
    Digipeat,
}

/// <summary>
/// Sends a raw AX.25 frame over RF.  Implemented by
/// <see cref="FrameTransmitService"/>, which routes to whichever backend
/// (native sound modem or external KISS TNC) is available.
/// </summary>
public interface IFrameTransmitter
{
    /// <summary>
    /// Queues the frame for transmission.  <paramref name="channel"/> selects
    /// the radio (KISS channel number); when no radio matches, any
    /// TX-capable backend is used.  Returns <see langword="false"/> when
    /// nothing can transmit.
    /// </summary>
    bool TrySend(byte[] ax25Frame, int channel = 0);

    /// <summary>
    /// Queues the frame with an explicit priority.  When
    /// <paramref name="txCompletion"/> is supplied it completes once the frame
    /// has actually left the radio (LAPB starts T1 there, immune to CSMA and
    /// queue delays) — or is cancelled if the frame never will.
    /// <paramref name="exactChannelOnly"/> disables the any-radio fallback:
    /// session traffic is bound to one frequency and must never silently hop
    /// radios.
    /// </summary>
    bool TrySend(
        byte[] ax25Frame,
        int channel,
        TxPriority priority,
        TaskCompletionSource<bool>? txCompletion,
        bool exactChannelOnly = false);
}

/// <summary>
/// Routes outbound frames to the native sound modem when it is running with
/// TX enabled, falling back to the external KISS TCP TNC otherwise.  Never
/// both — that would key two radios with the same packet.
/// </summary>
/// <remarks>
/// The modem is resolved lazily: the transmitter sits between the ingest
/// pipeline (which the modem feeds) and the services that consume it, so a
/// constructor reference would form a DI cycle.
/// </remarks>
public sealed class FrameTransmitService(
    IServiceProvider services,
    KissConnectionHolder kissConnectionHolder,
    ILogger<FrameTransmitService> logger) : IFrameTransmitter
{
    /// <summary>
    /// Largest AX.25 frame we will key up for: 70 bytes of addresses (source,
    /// destination, 8 digipeaters), 2 control, 1 PID, 256 info — rounded up.
    /// A 64 KiB KISS frame otherwise became 444 s of audio.
    /// </summary>
    public const int MaxFrameBytes = 330;

    private SoundModemService? _modemService;

    /// <summary>
    /// The single choke point every producer (KISS, AGWPE, beacons, messages,
    /// digipeater, LAPB) passes through: reject frames that are oversized or
    /// do not decode as AX.25 before they can reach a transmitter.
    /// </summary>
    internal static bool IsTransmittable(byte[] ax25Frame) =>
        ax25Frame.Length <= MaxFrameBytes && Modem.Ax25.Ax25Decoder.TryDecode(ax25Frame, out _);

    public bool TrySend(byte[] ax25Frame, int channel = 0) =>
        TrySend(ax25Frame, channel, TxPriority.Normal, txCompletion: null);

    public bool TrySend(
        byte[] ax25Frame,
        int channel,
        TxPriority priority,
        TaskCompletionSource<bool>? txCompletion,
        bool exactChannelOnly = false)
    {
        if (!IsTransmittable(ax25Frame))
        {
            logger.LogWarning(
                "Refusing to transmit a {Bytes}-byte frame: over {Max} bytes or not valid AX.25.",
                ax25Frame.Length, MaxFrameBytes);
            txCompletion?.TrySetCanceled();
            return false;
        }

        _modemService ??= services.GetRequiredService<SoundModemService>();

        if (_modemService.TryEnqueueTransmit(ax25Frame, channel, priority, txCompletion, exactChannelOnly))
            return true;

        if (kissConnectionHolder.TrySend(ax25Frame))
        {
            // The external TNC gives no airtime signal, so complete now: T1
            // starts at enqueue, which errs early — the conservative direction
            // (an early retransmit beats a hung timer). Operators on external
            // TNCs should configure a slightly larger T1.
            txCompletion?.TrySetResult(true);
            return true;
        }

        return false;
    }
}
