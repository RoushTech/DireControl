namespace DireControl.Api.Services.Weather;

/// <summary>
/// Pure decision logic for lightning proximity alerts, kept static so it can be
/// tested without constructing the background service.
/// </summary>
public static class LightningAlertLogic
{
    private const double EarthRadiusKm = 6371;
    private const double KmPerDegreeLat = 111.32;

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
    /// Bounding box covering <paramref name="radiusKm"/> around a point, suitable for
    /// <see cref="LightningStrikeBuffer.Query"/>. The longitude span widens with latitude
    /// and degenerates to the whole world near the poles; the buffer handles both a
    /// wrapped box (minLon &gt; maxLon) and a ≥ 360° span.
    /// </summary>
    public static (double MinLat, double MaxLat, double MinLon, double MaxLon) BoundingBox(
        double lat, double lon, double radiusKm)
    {
        var dLat = radiusKm / KmPerDegreeLat;
        var minLat = Math.Max(-90, lat - dLat);
        var maxLat = Math.Min(90, lat + dLat);

        var cosLat = Math.Cos(ToRad(lat));
        var dLon = cosLat <= 0.01 ? 180 : Math.Min(180, dLat / cosLat);
        return (minLat, maxLat, lon - dLon, lon + dLon);
    }

    /// <summary>The strike closest to home that lies within <paramref name="radiusKm"/>, or null.</summary>
    public static (LightningStrike Strike, double DistanceKm)? FindClosest(
        IEnumerable<LightningStrike> strikes, double homeLat, double homeLon, double radiusKm)
    {
        (LightningStrike Strike, double DistanceKm)? closest = null;
        foreach (var strike in strikes)
        {
            var distanceKm = HaversineKm(homeLat, homeLon, strike.Latitude, strike.Longitude);
            if (distanceKm > radiusKm)
                continue;
            if (closest is null || distanceKm < closest.Value.DistanceKm)
                closest = (strike, distanceKm);
        }
        return closest;
    }

    public static bool CooldownElapsed(DateTime nowUtc, DateTime? lastAlertUtc, TimeSpan cooldown)
        => lastAlertUtc is null || nowUtc - lastAlertUtc.Value >= cooldown;

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}
