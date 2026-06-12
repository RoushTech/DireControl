using DireControl.Enums;

namespace DireControl.Api.Services;

/// <summary>
/// Bootstrap configuration, bound from appsettings (override locally via
/// appsettings.local.json). The split with the <c>UserSetting</c> table is
/// deliberate: values here define the station's identity and host environment
/// (callsign, home position, Direwolf endpoint, retry/cleanup policy) and
/// require a restart; everything a user tunes at runtime (APRS-IS connection,
/// retention, weather keys, outbound path) lives in <c>UserSetting</c> and is
/// editable live from the Settings page.
/// </summary>
public class DireControlOptions
{
    public const string Section = "DireControl";

    public string OurCallsign { get; set; } = "N0CALL-10";
    public double? HomeLat { get; set; }
    public double? HomeLon { get; set; }
    public int StationExpiryTimeoutMinutes { get; set; } = 120;

    /// <summary>
    /// Per-type expiry thresholds (minutes). Falls back to StationExpiryTimeoutMinutes if a type is not listed.
    /// </summary>
    public Dictionary<string, int> StationExpiryByType { get; set; } = new()
    {
        ["Mobile"] = 120,
        ["Fixed"] = 240,
        ["Weather"] = 360,
        ["Digipeater"] = 240,
        ["IGate"] = 240,
        ["Gateway"] = 240,
    };

    /// <summary>Maximum number of retransmission attempts for unacknowledged outbound messages.</summary>
    public int MaxRetryAttempts { get; set; } = 5;

    /// <summary>Delay in seconds before the first retry. Doubles on each subsequent attempt.</summary>
    public int InitialRetryDelaySeconds { get; set; } = 30;

    /// <summary>
    /// How often the scheduled database cleanup (packet pruning + VACUUM) runs, in hours.
    /// 0 disables the schedule — cleanup then only runs when triggered manually.
    /// </summary>
    public int DatabaseCleanupIntervalHours { get; set; } = 24;

    /// <summary>
    /// Whether a cleanup run also runs VACUUM to reclaim freed disk space after pruning.
    /// VACUUM rewrites the whole file and needs a brief exclusive lock, so it only runs
    /// when packets were actually deleted.
    /// </summary>
    public bool VacuumOnCleanup { get; set; } = true;

    /// <summary>
    /// When true, a packet-reprocessing drain runs shortly after startup to re-derive any
    /// rows below the current parser version (e.g. after a parser fix + version bump).
    /// Default false: reprocessing is then only triggered manually via the maintenance API.
    /// </summary>
    public bool ReprocessStaleOnStartup { get; set; }

    public int GetExpiryMinutes(StationType stationType)
    {
        var key = stationType.ToString();
        return StationExpiryByType.TryGetValue(key, out var minutes) && minutes > 0
            ? minutes
            : StationExpiryTimeoutMinutes;
    }
}
