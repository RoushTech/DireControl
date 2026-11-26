using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

/// <summary>
/// What one radio's sound modem decoded during one UTC minute.  The modem's own
/// counters live in memory and restart with every capture session, and a frame
/// that failed its checksum is never stored as a packet, so these rows are the
/// only record of the bad-CRC rate over time.  Minutes with no frames at all
/// have no row.
/// </summary>
public class ModemRxMinute : IEntityTypeConfiguration<ModemRxMinute>
{
    public required string RadioId { get; set; }

    /// <summary>Start of the minute (UTC, seconds zero).</summary>
    public DateTime Minute { get; set; }

    /// <summary>Frames decoded with a passing checksum, repaired ones included.</summary>
    public int DecodedFrames { get; set; }

    /// <summary>Frames behind a real preamble that failed their checksum and could not be repaired.</summary>
    public int InvalidFrames { get; set; }

    /// <summary>Decoded frames that needed a bit repair to pass their checksum.</summary>
    public int RepairedFrames { get; set; }

    public void Configure(EntityTypeBuilder<ModemRxMinute> builder)
    {
        builder.HasKey(r => new { r.RadioId, r.Minute });
        builder.HasIndex(r => r.Minute);
    }
}
