namespace DireControl.Data.Models;

public class WeatherData
{
    public double? TemperatureF { get; set; }
    public int? HumidityPercent { get; set; }
    public double? WindSpeedMph { get; set; }
    public int? WindDirectionDeg { get; set; }
    public double? WindGustMph { get; set; }
    public double? PressureMbar { get; set; }
    public double? RainfallLastHourIn { get; set; }
    public double? RainfallLast24hIn { get; set; }
    public double? RainfallSinceMidnightIn { get; set; }
}
