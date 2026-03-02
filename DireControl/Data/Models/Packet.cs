using System.Text.Json;
using DireControl.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

public class Packet : IEntityTypeConfiguration<Packet>
{
    public int Id { get; set; }
    public required string StationCallsign { get; set; }
    public DateTime ReceivedAt { get; set; }
    public required string RawPacket { get; set; }
    public PacketType ParsedType { get; set; } = PacketType.Unknown;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string Path { get; set; } = string.Empty;
    public List<ResolvedPathEntry> ResolvedPath { get; set; } = [];
    public int HopCount { get; set; }
    public int UnknownHopCount { get; set; }
    public int KissChannel { get; set; }
    public string Comment { get; set; } = string.Empty;
    public WeatherData? WeatherData { get; set; }
    public TelemetryData? TelemetryData { get; set; }
    public MessageData? MessageData { get; set; }
    public SignalData? SignalData { get; set; }
    public string? GridSquare { get; set; }

    public Station Station { get; set; } = null!;

    public void Configure(EntityTypeBuilder<Packet> builder)
    {
        builder.HasOne(p => p.Station)
               .WithMany(s => s.Packets)
               .HasForeignKey(p => p.StationCallsign)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(p => p.StationCallsign);
        builder.HasIndex(p => p.ReceivedAt);

        builder.Property(p => p.ResolvedPath)
               .HasConversion(
                   v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                   v => JsonSerializer.Deserialize<List<ResolvedPathEntry>>(v, (JsonSerializerOptions?)null) ?? new List<ResolvedPathEntry>());
        builder.Property(p => p.WeatherData)
               .HasConversion(
                   v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                   v => JsonSerializer.Deserialize<WeatherData>(v, (JsonSerializerOptions?)null));
        builder.Property(p => p.TelemetryData)
               .HasConversion(
                   v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                   v => JsonSerializer.Deserialize<TelemetryData>(v, (JsonSerializerOptions?)null));
        builder.Property(p => p.MessageData)
               .HasConversion(
                   v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                   v => JsonSerializer.Deserialize<MessageData>(v, (JsonSerializerOptions?)null));
        builder.Property(p => p.SignalData)
               .HasConversion(
                   v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                   v => JsonSerializer.Deserialize<SignalData>(v, (JsonSerializerOptions?)null));
    }
}
