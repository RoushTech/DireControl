using DireControl.Enums;

namespace DireControl.Api.Services;

public class DireControlOptions
{
    public const string Section = "DireControl";

    public string OurCallsign { get; set; } = "N0CALL-10";
    public double? StationLatitude { get; set; }
    public double? StationLongitude { get; set; }
    public int StationExpiryTimeoutMinutes { get; set; } = 120;
    public List<string> CorsOrigins { get; set; } = [];

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
    };

    public int GetExpiryMinutes(StationType stationType)
    {
        var key = stationType.ToString();
        return StationExpiryByType.TryGetValue(key, out var minutes) && minutes > 0
            ? minutes
            : StationExpiryTimeoutMinutes;
    }
}
