using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

public class DigiConfirmation : IEntityTypeConfiguration<DigiConfirmation>
{
    public int Id { get; set; }
    public int OwnBeaconId { get; set; }
    public DateTime ConfirmedAt { get; set; }
    public required string DigipeaterCallsign { get; set; }
    public double? DigipeaterLat { get; set; }
    public double? DigipeaterLon { get; set; }
    public string? AliasUsed { get; set; }
    public int SecondsAfterBeacon { get; set; }

    public OwnBeacon OwnBeacon { get; set; } = null!;

    public void Configure(EntityTypeBuilder<DigiConfirmation> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.DigipeaterCallsign).IsRequired().HasMaxLength(16);
        builder.Property(c => c.AliasUsed).HasMaxLength(16);
        builder.HasOne(c => c.OwnBeacon)
               .WithMany(b => b.Confirmations)
               .HasForeignKey(c => c.OwnBeaconId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => c.OwnBeaconId);
    }
}
