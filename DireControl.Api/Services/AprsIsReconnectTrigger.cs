namespace DireControl.Api.Services;

/// <summary>
/// Singleton that allows external callers (e.g. <see cref="SettingsController"/>)
/// to signal <see cref="AprsIsService"/> to drop and re-establish its connection
/// immediately — for example when the filter or server settings change.
/// </summary>
public sealed class AprsIsReconnectTrigger
{
    private volatile CancellationTokenSource _cts = new();

    /// <summary>
    /// A <see cref="CancellationToken"/> that is cancelled whenever
    /// <see cref="Trigger"/> is called. A new token is available after each trigger.
    /// </summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Cancels the current token (instructing <see cref="AprsIsService"/> to
    /// reconnect) and replaces it with a fresh one for the next connection cycle.
    /// </summary>
    public void Trigger()
    {
        var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }
}
