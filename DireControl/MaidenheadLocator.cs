namespace DireControl;

/// <summary>
/// Converts coordinates to Maidenhead grid locators (e.g. "EM75va"):
/// field (20°×10°), square (2°×1°), subsquare (5'×2.5').
/// </summary>
public static class MaidenheadLocator
{
    /// <summary>
    /// Returns the 6-character locator for a position, or <see langword="null"/>
    /// for out-of-range or NaN coordinates.
    /// </summary>
    public static string? FromLatLon(double latitude, double longitude)
    {
        if (double.IsNaN(latitude) || double.IsNaN(longitude) ||
            latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180)
        {
            return null;
        }

        var lon = longitude + 180.0;
        var lat = latitude + 90.0;

        // The exact north pole / antimeridian fall into the last cell.
        if (lon >= 360.0) lon = Math.BitDecrement(360.0);
        if (lat >= 180.0) lat = Math.BitDecrement(180.0);

        var fieldLon = (char)('A' + (int)(lon / 20.0));
        var fieldLat = (char)('A' + (int)(lat / 10.0));
        var squareLon = (char)('0' + (int)(lon % 20.0 / 2.0));
        var squareLat = (char)('0' + (int)(lat % 10.0));
        var subLon = (char)('a' + (int)(lon % 2.0 * 12.0));
        var subLat = (char)('a' + (int)(lat % 1.0 * 24.0));

        return $"{fieldLon}{fieldLat}{squareLon}{squareLat}{subLon}{subLat}";
    }
}
