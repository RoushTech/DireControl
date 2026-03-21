using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

public class CoverageGridStatistic : IEntityTypeConfiguration<CoverageGridStatistic>
{
    public required string GridSquare { get; set; }
    public int PacketCount { get; set; }
    public double AvgLat { get; set; }
    public double AvgLon { get; set; }
    public DateTime LastComputedAt { get; set; }

    public void Configure(EntityTypeBuilder<CoverageGridStatistic> builder)
    {
        builder.HasKey(c => c.GridSquare);
    }
}
