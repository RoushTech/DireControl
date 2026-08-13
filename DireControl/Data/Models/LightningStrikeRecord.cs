using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

/// <summary>
/// A single lightning strike from the Blitzortung.org feed, persisted so the map can
/// replay strikes in sync with historical radar frames. Rows are pruned past the
/// retention window by the ingest pipeline.
/// </summary>
public class LightningStrikeRecord : IEntityTypeConfiguration<LightningStrikeRecord>
{
    public long Id { get; set; }
    public DateTime TimeUtc { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    public void Configure(EntityTypeBuilder<LightningStrikeRecord> builder)
    {
        builder.HasKey(s => s.Id);
        builder.HasIndex(s => s.TimeUtc);
    }
}
