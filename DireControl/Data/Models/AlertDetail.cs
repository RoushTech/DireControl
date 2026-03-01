namespace DireControl.Data.Models;

/// <summary>Alert-specific payload. Fields populated vary by <see cref="Enums.AlertType"/>.</summary>
public class AlertDetail
{
    public double? DistanceMeters { get; set; }
    public double? StationLat { get; set; }
    public double? StationLon { get; set; }
    public string? GeofenceName { get; set; }
    public string? MessageText { get; set; }
    /// <summary>"entered" or "exited" for Geofence alerts.</summary>
    public string? Direction { get; set; }
    /// <summary>Name of the matched proximity rule.</summary>
    public string? RuleName { get; set; }
}
