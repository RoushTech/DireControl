using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

/// <summary>
/// One day's direct-RF reception summary for a single receiving radio (KISS channel).
/// These rows are the durable series behind the RF Heard trend chart — packets are pruned
/// according to the retention settings, but the daily rollups are kept indefinitely so the
/// long-term effect of an antenna or radio change stays visible.
/// </summary>
public class RfHeardDaily : IEntityTypeConfiguration<RfHeardDaily>
{
    /// <summary>KISS channel of the radio that did the hearing.</summary>
    public int ChannelNumber { get; set; }

    /// <summary>
    /// The bucket's local calendar day.  Deliberately a <see cref="DateOnly"/> and not a
    /// <see cref="DateTime"/>: a calendar day is not an instant, so there is no meaningful
    /// UTC value to store for it.
    /// </summary>
    public DateOnly Day { get; set; }

    /// <summary>Distinct stations heard direct on this channel during the day.</summary>
    public int UniqueDirectStations { get; set; }

    /// <summary>
    /// Stations heard direct on this channel for the first time ever during the day —
    /// shows whether a change is reaching genuinely new stations rather than just more
    /// packets from the same ones.
    /// </summary>
    public int NewDirectStations { get; set; }

    /// <summary>Total direct packets received on this channel during the day.</summary>
    public int DirectPackets { get; set; }

    /// <summary>Farthest direct-heard station that day, km from home. Null when unknown.</summary>
    public double? MaxDirectDistanceKm { get; set; }

    /// <summary>Median distance of direct-heard stations that day, km from home.</summary>
    public double? MedianDirectDistanceKm { get; set; }

    /// <summary>
    /// How many of <see cref="UniqueDirectStations"/> had a usable position — the
    /// denominator behind the distance figures, so they can be read honestly.
    /// </summary>
    public int DirectStationsWithPosition { get; set; }

    public DateTime LastComputedAt { get; set; }

    public void Configure(EntityTypeBuilder<RfHeardDaily> builder)
    {
        builder.HasKey(r => new { r.ChannelNumber, r.Day });
        builder.HasIndex(r => r.Day);
    }
}
