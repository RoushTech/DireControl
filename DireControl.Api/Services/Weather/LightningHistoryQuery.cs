using DireControl.Data.Models;

namespace DireControl.Api.Services.Weather;

/// <summary>
/// Composable, SQL-translatable filters for querying persisted lightning strikes.
/// Mirrors the bounding-box semantics of <see cref="LightningStrikeBuffer.Query"/>:
/// handles antimeridian-crossing boxes (minLon &gt; maxLon after normalisation) and
/// Leaflet's unwrapped longitudes.
/// </summary>
public static class LightningHistoryQuery
{
    public static IQueryable<LightningStrikeRecord> ApplyWindow(
        this IQueryable<LightningStrikeRecord> query, DateTime fromUtc, DateTime toUtc)
        => query.Where(s => s.TimeUtc >= fromUtc && s.TimeUtc <= toUtc);

    public static IQueryable<LightningStrikeRecord> ApplyBounds(
        this IQueryable<LightningStrikeRecord> query,
        double minLat, double maxLat, double minLon, double maxLon)
    {
        query = query.Where(s => s.Latitude >= minLat && s.Latitude <= maxLat);

        if (maxLon - minLon >= 360)
            return query;

        minLon = NormalizeLon(minLon);
        maxLon = NormalizeLon(maxLon);

        return minLon <= maxLon
            ? query.Where(s => s.Longitude >= minLon && s.Longitude <= maxLon)
            : query.Where(s => s.Longitude >= minLon || s.Longitude <= maxLon);
    }

    private static double NormalizeLon(double lon)
    {
        lon %= 360;
        if (lon >= 180) lon -= 360;
        if (lon < -180) lon += 360;
        return lon;
    }
}
