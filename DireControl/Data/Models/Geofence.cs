namespace DireControl.Data.Models;

public class Geofence
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public double CenterLat { get; set; }
    public double CenterLon { get; set; }
    public double RadiusMeters { get; set; }
    public bool IsActive { get; set; }
    public bool AlertOnEnter { get; set; }
    public bool AlertOnExit { get; set; }
}
