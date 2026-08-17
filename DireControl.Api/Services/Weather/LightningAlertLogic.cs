namespace DireControl.Api.Services.Weather;

/// <summary>
/// Pure decision logic for lightning proximity alerts, kept static so it can be
/// tested without constructing the background service.
/// </summary>
public static class LightningAlertLogic
{
    private const double EarthRadiusKm = 6371;

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return EarthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Initial great-circle bearing from (lat1, lon1) to (lat2, lon2) in degrees, 0–360.</summary>
    public static double BearingDegrees(double lat1, double lon1, double lat2, double lon2)
    {
        var dLon = ToRad(lon2 - lon1);
        var y = Math.Sin(dLon) * Math.Cos(ToRad(lat2));
        var x = Math.Cos(ToRad(lat1)) * Math.Sin(ToRad(lat2)) -
                Math.Sin(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Cos(dLon);
        var bearing = Math.Atan2(y, x) * 180.0 / Math.PI;
        return (bearing + 360) % 360;
    }

    /// <summary>
    /// Whether a strike should raise an alert, and how far away it is. Evaluated per strike
    /// as it arrives from the feed, so a strike is only ever judged once — the alert fires
    /// on the first strike to breach the radius rather than the closest one in a poll window.
    /// </summary>
    /// <param name="lastAlertedStrike">
    /// The strike that raised the previous alert. The feed can redeliver a strike, and an
    /// identical one must never alert twice regardless of how short the cooldown is.
    /// </param>
    public static (bool ShouldAlert, double DistanceKm) Evaluate(
        LightningStrike strike,
        double homeLat, double homeLon, double radiusKm,
        DateTime nowUtc, DateTime? lastAlertUtc, TimeSpan cooldown,
        LightningStrike? lastAlertedStrike)
    {
        var distanceKm = HaversineKm(homeLat, homeLon, strike.Latitude, strike.Longitude);
        if (distanceKm > radiusKm)
            return (false, distanceKm);
        if (lastAlertedStrike == strike)
            return (false, distanceKm);
        return (CooldownElapsed(nowUtc, lastAlertUtc, cooldown), distanceKm);
    }

    public static bool CooldownElapsed(DateTime nowUtc, DateTime? lastAlertUtc, TimeSpan cooldown)
        => lastAlertUtc is null || nowUtc - lastAlertUtc.Value >= cooldown;

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}
