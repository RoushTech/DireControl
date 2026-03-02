using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

public class OwnBeacon : IEntityTypeConfiguration<OwnBeacon>
{
    public int Id { get; set; }
    public required string RadioId { get; set; }
    public DateTime BeaconedAt { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int? Heading { get; set; }
    public double? Speed { get; set; }
    public string? Comment { get; set; }
    public string? PathUsed { get; set; }
    public int HopCount { get; set; }

    public Radio Radio { get; set; } = null!;
    public ICollection<DigiConfirmation> Confirmations { get; set; } = [];

    public void Configure(EntityTypeBuilder<OwnBeacon> builder)
    {
        builder.HasKey(b => b.Id);
        builder.Property(b => b.RadioId).IsRequired().HasMaxLength(36);
        builder.HasOne(b => b.Radio)
               .WithMany(r => r.Beacons)
               .HasForeignKey(b => b.RadioId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(b => new { b.RadioId, b.BeaconedAt });
    }
}
