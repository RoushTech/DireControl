using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

public class ProximityRule : IEntityTypeConfiguration<ProximityRule>
{
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>If non-null, only checks this specific callsign; otherwise checks any station.</summary>
    public string? TargetCallsign { get; set; }
    public double CenterLat { get; set; }
    public double CenterLon { get; set; }
    public double RadiusMetres { get; set; }
    public bool IsActive { get; set; }

    public void Configure(EntityTypeBuilder<ProximityRule> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Name).HasMaxLength(100);
        builder.Property(r => r.TargetCallsign).HasMaxLength(16);
    }
}
