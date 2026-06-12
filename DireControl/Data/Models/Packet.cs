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

    /// <summary>
    /// Version of the parsing logic that last populated this row's derived fields.
    /// Rows whose value is below <c>ParserVersionInfo.Current</c> were parsed by an
    /// older (potentially buggy) parser and are eligible for reprocessing. A value of
    /// 0 means the row predates parser versioning.
    /// </summary>
    public int ParserVersion { get; set; }

    /// <summary>How this packet reached DireControl (RF via Direwolf KISS, or APRS-IS).</summary>
    public PacketSource Source { get; set; } = PacketSource.Rf;

    /// <summary>
    /// Everything after the first ':' in the TNC2 string — the APRS info field.
    /// Stored for efficient deduplication across RF and APRS-IS paths.
    /// </summary>
    public string InfoField { get; set; } = string.Empty;

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
        builder.HasIndex(p => new { p.StationCallsign, p.ReceivedAt });
        builder.HasIndex(p => p.ParserVersion);
        // Serves the parser's ParsedType=Unknown poll and message-history queries;
        // without it both full-scan the table.
        builder.HasIndex(p => new { p.ParsedType, p.ReceivedAt });

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
        // Mapped as a native EF JSON column (same on-disk format as the converter
        // pattern) so WHERE clauses on Addressee/Text translate to json_extract
        // instead of throwing at runtime.
        builder.OwnsOne(p => p.MessageData, mb => mb.ToJson());
        builder.Property(p => p.SignalData)
               .HasConversion(
                   v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                   v => JsonSerializer.Deserialize<SignalData>(v, (JsonSerializerOptions?)null));
    }
}
