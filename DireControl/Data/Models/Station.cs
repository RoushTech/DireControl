using System.Text.Json;
using DireControl.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

public class Station : IEntityTypeConfiguration<Station>
{
    public required string Callsign { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public double? LastLat { get; set; }
    public double? LastLon { get; set; }
    public int? LastHeading { get; set; }
    public double? LastSpeed { get; set; }
    public double? LastAltitude { get; set; }
    public required string Symbol { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsWeatherStation { get; set; }
    public StationType StationType { get; set; } = StationType.Unknown;
    public QrzLookupData? QrzLookupData { get; set; }
    public bool IsOnWatchList { get; set; }
    public string? GridSquare { get; set; }
    public HeardVia HeardVia { get; set; } = HeardVia.Unknown;

    public ICollection<Packet> Packets { get; set; } = [];
    public StationStatistic? Statistics { get; set; }

    public void Configure(EntityTypeBuilder<Station> builder)
    {
        builder.HasKey(s => s.Callsign);
        builder.Property(s => s.Callsign).HasMaxLength(16);
        builder.Property(s => s.Symbol).HasMaxLength(2);
        builder.Property(s => s.QrzLookupData)
               .HasConversion(
                   v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                   v => JsonSerializer.Deserialize<QrzLookupData>(v, (JsonSerializerOptions?)null));
    }
}
