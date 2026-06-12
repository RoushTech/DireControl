using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

public class StationStatistic : IEntityTypeConfiguration<StationStatistic>
{
    public required string Callsign { get; set; }
    public int PacketsToday { get; set; }
    // Maintained incrementally by the parser; seeded with a one-time COUNT when 0.
    public int TotalPackets { get; set; }
    public double AveragePacketsPerHour { get; set; }
    public int LongestGapMinutes { get; set; }
    public DateTime LastComputedAt { get; set; }

    public Station Station { get; set; } = null!;

    public void Configure(EntityTypeBuilder<StationStatistic> builder)
    {
        builder.HasKey(ss => ss.Callsign);
        builder.HasOne(ss => ss.Station)
               .WithOne(s => s.Statistics)
               .HasForeignKey<StationStatistic>(ss => ss.Callsign)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
