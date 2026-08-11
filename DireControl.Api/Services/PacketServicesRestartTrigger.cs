namespace DireControl.Api.Services;

/// <summary>
/// Singleton that lets <see cref="Controllers.SettingsController"/> signal the
/// packet application services (PMS host, AGWPE server) to tear down and
/// re-read their configuration after a settings save.
/// </summary>
public sealed class PacketServicesRestartTrigger
{
    private volatile CancellationTokenSource _cts = new();

    /// <summary>
    /// A <see cref="CancellationToken"/> that is cancelled whenever
    /// <see cref="Trigger"/> is called. A new token is available after each trigger.
    /// </summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Cancels the current token (instructing the packet services to restart)
    /// and replaces it with a fresh one for the next cycle.
    /// </summary>
    public void Trigger()
    {
        var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }
}
