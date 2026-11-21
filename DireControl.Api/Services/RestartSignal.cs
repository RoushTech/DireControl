namespace DireControl.Api.Services;

/// <summary>
/// A restart/reconnect signal: services link their loop token to
/// <see cref="Token"/> and re-read their configuration when it is cancelled;
/// settings endpoints call <see cref="Trigger"/>.  One class, registered once
/// per key in <see cref="RestartSignals"/>.
/// </summary>
public sealed class RestartSignal
{
    private volatile CancellationTokenSource _cts = new();

    /// <summary>Cancelled whenever <see cref="Trigger"/> is called; a fresh token follows.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Cancels the current token and replaces it for the next cycle.</summary>
    public void Trigger()
    {
        var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        old.Cancel();
        // Deliberately not disposed: a reader between loading _cts and reading
        // .Token would get ObjectDisposedException, which (StopHost behaviour)
        // took the whole app down.  An undisposed, cancelled CTS with no timer
        // holds nothing the GC cannot reclaim.
    }
}

/// <summary>
/// DI keys for the <see cref="RestartSignal"/> instances, and who listens.
/// </summary>
public static class RestartSignals
{
    /// <summary>Sound modem instances (<see cref="SoundModemService"/>): radio and RF-service edits.</summary>
    public const string Modem = "modem";

    /// <summary>
    /// KISS TCP server (<see cref="KissTcpServerService"/>): only its own settings
    /// (the RF-services save).  Kept apart from <see cref="Modem"/> so radio
    /// edits and modem restarts no longer drop every KISS client.
    /// </summary>
    public const string KissServer = "kiss-server";

    /// <summary>
    /// Packet services: PMS host, AGWPE server and the terminal listener —
    /// packet-settings saves and callsign changes.
    /// </summary>
    public const string PacketServices = "packet-services";

    /// <summary>APRS-IS receive and transmit connections.</summary>
    public const string AprsIs = "aprs-is";

    /// <summary>The external KISS TNC client (<see cref="KissTcpService"/>).</summary>
    public const string KissClient = "kiss-client";
}
