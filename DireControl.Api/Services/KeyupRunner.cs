using DireControl.Modem.Audio;
using DireControl.Modem.Ptt;

namespace DireControl.Api.Services;

/// <summary>Outcome of one keyup.</summary>
internal enum KeyupOutcome
{
    Unknown = 0,

    /// <summary>Audio played out and PTT dropped.</summary>
    Transmitted = 1,

    /// <summary>The playback device could not be opened; PTT was never keyed.</summary>
    OpenFailed = 2,

    /// <summary>Keying PTT failed.</summary>
    KeyFailed = 3,

    /// <summary>Playback failed part-way; PTT was dropped afterwards.</summary>
    PlaybackFailed = 4,

    /// <summary>
    /// PTT could not be released, even after retries — the radio may still be
    /// transmitting.  The only outcome that must tear the modem session down.
    /// </summary>
    UnkeyFailed = 5,
}

internal readonly record struct KeyupResult(KeyupOutcome Outcome, Exception? Error = null, bool WatchdogTripped = false);

/// <summary>
/// Runs one transmission: open the playback device, key PTT, play, drain,
/// unkey.  The ordering is the point — the device is opened <em>before</em>
/// keying, so a device that cannot be opened never puts dead carrier on the
/// air — and an independent watchdog unkeys if playback stalls, because a
/// drain blocked inside the sound driver cannot be cancelled.
/// </summary>
internal static class KeyupRunner
{
    /// <summary>Absolute ceiling on one keyup, whatever its audio length.</summary>
    internal static readonly TimeSpan MaxKeyup = TimeSpan.FromSeconds(30);

    private const int UnkeyAttempts = 3;
    private static readonly TimeSpan UnkeyRetryDelay = TimeSpan.FromMilliseconds(330);

    /// <summary>
    /// How long PTT may stay keyed for <paramref name="audio"/> of airtime:
    /// the audio plus max(2 s, 20 %) of slack for device latency, capped at
    /// <see cref="MaxKeyup"/>.
    /// </summary>
    internal static TimeSpan WatchdogLimit(TimeSpan audio)
    {
        var slack = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(2).Ticks, audio.Ticks / 5));
        var limit = audio + slack;
        return limit < MaxKeyup ? limit : MaxKeyup;
    }

    internal static KeyupResult Run(
        IPttController? ptt,
        Func<IPlaybackSink> openSink,
        ReadOnlySpan<float> audio,
        TimeSpan watchdogLimit,
        ILogger logger,
        string radioName,
        Action? onKeyed = null)
    {
        IPlaybackSink sink;
        try
        {
            sink = openSink();
        }
        catch (Exception ex)
        {
            return new KeyupResult(KeyupOutcome.OpenFailed, ex);
        }

        using (sink)
        {
            var watchdogTripped = 0;
            using var watchdog = ptt is null
                ? null
                : new Timer(_ =>
                {
                    Interlocked.Exchange(ref watchdogTripped, 1);
                    logger.LogError(
                        "{Radio} PTT watchdog: still keyed after {Seconds:F1} s — forcing unkey.",
                        radioName, watchdogLimit.TotalSeconds);
                    try { ptt.SetPtt(false); }
                    catch (Exception ex) { logger.LogError(ex, "{Radio} watchdog unkey failed.", radioName); }
                }, null, Timeout.Infinite, Timeout.Infinite);

            var outcome = KeyupOutcome.Transmitted;
            Exception? error = null;
            try
            {
                ptt?.SetPtt(true);
            }
            catch (Exception ex)
            {
                outcome = KeyupOutcome.KeyFailed;
                error = ex;
            }

            if (outcome == KeyupOutcome.Transmitted)
            {
                watchdog?.Change(watchdogLimit, Timeout.InfiniteTimeSpan);
                try
                {
                    onKeyed?.Invoke();
                    sink.Write(audio);
                    sink.Drain();
                }
                catch (Exception ex)
                {
                    outcome = KeyupOutcome.PlaybackFailed;
                    error = ex;
                }
                watchdog?.Change(Timeout.Infinite, Timeout.Infinite);
            }

            if (ptt is not null && !TryUnkey(ptt, logger, radioName, out var unkeyError))
                return new KeyupResult(KeyupOutcome.UnkeyFailed, unkeyError, watchdogTripped == 1);

            return new KeyupResult(outcome, error, watchdogTripped == 1);
        }
    }

    /// <summary>Releases PTT, retrying <see cref="UnkeyAttempts"/> times over about a second.</summary>
    internal static bool TryUnkey(IPttController ptt, ILogger logger, string radioName, out Exception? lastError)
    {
        lastError = null;
        for (var attempt = 1; attempt <= UnkeyAttempts; attempt++)
        {
            try
            {
                ptt.SetPtt(false);
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex;
                logger.LogError(ex, "{Radio} failed to unkey PTT (attempt {Attempt}/{Max}).",
                    radioName, attempt, UnkeyAttempts);
                if (attempt < UnkeyAttempts)
                    Thread.Sleep(UnkeyRetryDelay);
            }
        }
        return false;
    }
}
