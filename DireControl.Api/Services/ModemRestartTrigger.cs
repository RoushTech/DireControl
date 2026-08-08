namespace DireControl.Api.Services;

/// <summary>
/// Singleton that allows external callers (e.g. <see cref="SettingsController"/>)
/// to signal <see cref="SoundModemService"/> to tear down and re-open the audio
/// device immediately — for example when the modem settings change.
/// </summary>
public sealed class ModemRestartTrigger
{
    private volatile CancellationTokenSource _cts = new();

    /// <summary>
    /// A <see cref="CancellationToken"/> that is cancelled whenever
    /// <see cref="Trigger"/> is called. A new token is available after each trigger.
    /// </summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Cancels the current token (instructing <see cref="SoundModemService"/> to
    /// restart) and replaces it with a fresh one for the next capture cycle.
    /// </summary>
    public void Trigger()
    {
        var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }
}
