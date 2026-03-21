using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

public class DigipeaterStatistic : IEntityTypeConfiguration<DigipeaterStatistic>
{
    public required string Callsign { get; set; }
    public int TotalPacketsForwarded { get; set; }
    public int Last24hPackets { get; set; }
    public double HopPositionSum { get; set; }
    public int HopPositionCount { get; set; }
    public DateTime LastComputedAt { get; set; }

    public double AverageHopPosition => HopPositionCount > 0 ? HopPositionSum / HopPositionCount : 0.0;

    public void Configure(EntityTypeBuilder<DigipeaterStatistic> builder)
    {
        builder.HasKey(d => d.Callsign);
        builder.Ignore(d => d.AverageHopPosition);
    }
}
