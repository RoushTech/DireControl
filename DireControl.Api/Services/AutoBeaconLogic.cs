namespace DireControl.Api.Services;

/// <summary>
/// Pure scheduling logic for automatic beaconing, extracted as static functions
/// so the "is a beacon due?" decision is unit-testable without the background
/// service, database, or clock.
/// </summary>
public static class AutoBeaconLogic
{
    /// <summary>Smallest auto-beacon interval accepted, to avoid flooding RF.</summary>
    public const int MinIntervalSeconds = 60;

    /// <summary>
    /// Decides whether an automatic beacon is due at <paramref name="now"/>.
    /// A beacon is due when the radio has never beaconed and never been attempted,
    /// or when at least <paramref name="intervalSeconds"/> have elapsed since the
    /// most recent of the last successful beacon and the last attempt.
    /// </summary>
    /// <param name="now">Current UTC time.</param>
    /// <param name="lastBeaconAt">When this radio last successfully beaconed, or null.</param>
    /// <param name="lastAttemptAt">
    /// When the service last attempted a beacon for this radio this run, or null.
    /// Tracked separately from <paramref name="lastBeaconAt"/> so a misconfigured
    /// radio (no home position / no TX backend) is retried at most once per
    /// interval rather than on every poll.
    /// </param>
    /// <param name="intervalSeconds">Configured beacon interval in seconds.</param>
    public static bool IsDue(
        DateTime now,
        DateTime? lastBeaconAt,
        DateTime? lastAttemptAt,
        int intervalSeconds)
    {
        var interval = intervalSeconds < MinIntervalSeconds ? MinIntervalSeconds : intervalSeconds;

        DateTime? reference = (lastBeaconAt, lastAttemptAt) switch
        {
            (null, null) => null,
            ({ } b, null) => b,
            (null, { } a) => a,
            ({ } b, { } a) => b > a ? b : a,
        };

        if (reference is null)
            return true;

        return (now - reference.Value).TotalSeconds >= interval;
    }
}
