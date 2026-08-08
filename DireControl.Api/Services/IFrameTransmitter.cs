namespace DireControl.Api.Services;

/// <summary>
/// Sends a raw AX.25 UI frame over RF.  Implemented by
/// <see cref="FrameTransmitService"/>, which routes to whichever backend
/// (native sound modem or external KISS TNC) is available.
/// </summary>
public interface IFrameTransmitter
{
    /// <summary>
    /// Queues the frame for transmission.  Returns <see langword="false"/>
    /// when no transmit-capable backend is available.
    /// </summary>
    bool TrySend(byte[] ax25Frame);
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
    KissConnectionHolder kissConnectionHolder) : IFrameTransmitter
{
    private SoundModemService? _modemService;

    public bool TrySend(byte[] ax25Frame)
    {
        _modemService ??= services.GetRequiredService<SoundModemService>();
        return _modemService.TryEnqueueTransmit(ax25Frame) || kissConnectionHolder.TrySend(ax25Frame);
    }
}
