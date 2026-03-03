using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

public class Radio : IEntityTypeConfiguration<Radio>
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Name { get; set; }
    public required string Callsign { get; set; }
    public string? Ssid { get; set; }
    public string FullCallsign { get; set; } = string.Empty;
    public int ChannelNumber { get; set; } = 0;
    public string? Notes { get; set; }
    public string? BeaconPath { get; set; }
    public string? BeaconSymbol { get; set; }
    public string? BeaconComment { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public int ExpectedIntervalSeconds { get; set; } = 600;

    public ICollection<OwnBeacon> Beacons { get; set; } = [];

    public static string ComputeFullCallsign(string callsign, string? ssid) =>
        string.IsNullOrEmpty(ssid) ? callsign : $"{callsign}-{ssid}";

    public void Configure(EntityTypeBuilder<Radio> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasMaxLength(36);
        builder.Property(r => r.Name).IsRequired().HasMaxLength(100);
        builder.Property(r => r.Callsign).IsRequired().HasMaxLength(16);
        builder.Property(r => r.Ssid).HasMaxLength(4);
        builder.Property(r => r.FullCallsign).IsRequired().HasMaxLength(20);
        builder.Property(r => r.Notes).HasMaxLength(500);
        builder.Property(r => r.BeaconPath).HasMaxLength(100);
        builder.Property(r => r.BeaconSymbol).HasMaxLength(2);
        builder.Property(r => r.BeaconComment).HasMaxLength(256);
        builder.HasIndex(r => r.FullCallsign);
        builder.HasIndex(r => r.IsActive);
    }
}
