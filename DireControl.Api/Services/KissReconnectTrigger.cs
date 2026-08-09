namespace DireControl.Api.Services;

/// <summary>
/// Singleton that lets <see cref="SettingsController"/> signal
/// <see cref="KissTcpService"/> to drop and re-establish its external-TNC
/// connection immediately — for example when the KISS TCP client settings change.
/// </summary>
public sealed class KissReconnectTrigger
{
    private volatile CancellationTokenSource _cts = new();

    /// <summary>
    /// A <see cref="CancellationToken"/> that is cancelled whenever
    /// <see cref="Trigger"/> is called.  A fresh token is available afterwards.
    /// </summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Cancels the current token (instructing <see cref="KissTcpService"/> to
    /// re-read settings and reconnect) and replaces it with a fresh one.
    /// </summary>
    public void Trigger()
    {
        var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }
}
