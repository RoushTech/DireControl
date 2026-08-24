using DireControl.Data.Models;
using DireControl.PathParsing;

namespace DireControl.Api.Services;

/// <summary>
/// Pure helpers behind <see cref="RfHeardAggregationService"/>, kept separate so the
/// bucketing, distance, and own-station rules are testable without standing up the
/// background service.
/// </summary>
public static class RfHeardLogic
{
    /// <summary>
    /// The local calendar day a UTC instant falls in.  Reception is bucketed by local day
    /// (not UTC day) so the trend chart lines up with the operator's own "yesterday".
    /// </summary>
    public static DateOnly LocalDay(DateTime utc) =>
        DateOnly.FromDateTime(
            (utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc))
                .ToLocalTime());

    /// <summary>UTC half-open range <c>[start, end)</c> covering a local calendar day.</summary>
    public static (DateTime StartUtc, DateTime EndUtc) LocalDayRangeUtc(DateOnly day)
    {
        var localStart = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        var localEnd = day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        return (localStart.ToUniversalTime(), localEnd.ToUniversalTime());
    }

    /// <summary>
    /// Median of <paramref name="values"/>, or <c>null</c> when empty.  Even counts return
    /// the mean of the two middle values.  The input is copied before sorting.
    /// </summary>
    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return null;

        var sorted = values.ToArray();
        Array.Sort(sorted);

        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>
    /// Picks the position to measure a direct copy against: the packet's own reported
    /// position when it carried one (correct for mobiles, which may have been heard from
    /// somewhere other than where they are now), otherwise the station's last known fix.
    /// </summary>
    public static (double Lat, double Lon)? PickPosition(
        double? packetLat, double? packetLon,
        double? stationLat, double? stationLon)
    {
        if (packetLat is { } plat && packetLon is { } plon)
            return (plat, plon);

        if (stationLat is { } slat && stationLon is { } slon)
            return (slat, slon);

        return null;
    }

    /// <summary>
    /// True when <paramref name="callsign"/> belongs to one of our own radios.  Own beacons
    /// digipeated back to us arrive as ordinary RF packets, and counting them would make
    /// every radio look like it hears itself.  Uses <see cref="CallsignMatcher"/> so the
    /// "-0 SSID is no SSID" rule applies.
    /// </summary>
    public static bool IsOwnStation(string callsign, IEnumerable<Radio> radios, string? ourCallsign)
    {
        if (string.IsNullOrWhiteSpace(callsign))
            return false;

        if (!string.IsNullOrWhiteSpace(ourCallsign) &&
            CallsignMatcher.Normalise(ourCallsign) == CallsignMatcher.Normalise(callsign))
        {
            return true;
        }

        return radios.Any(r => CallsignMatcher.Matches(r, callsign));
    }
}
