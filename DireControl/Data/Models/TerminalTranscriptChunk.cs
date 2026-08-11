using DireControl.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

/// <summary>
/// A run of session bytes in one direction — the transcript recorder
/// coalesces same-direction data into chunks rather than storing per-keystroke
/// rows.  Replay is the ordered concatenation of a record's chunks.
/// </summary>
public class TerminalTranscriptChunk : IEntityTypeConfiguration<TerminalTranscriptChunk>
{
    public int Id { get; set; }
    public int TerminalSessionRecordId { get; set; }
    public TerminalSessionRecord? TerminalSessionRecord { get; set; }
    public DateTime Timestamp { get; set; }
    public TranscriptDirection Direction { get; set; }
    public byte[] Data { get; set; } = [];

    public void Configure(EntityTypeBuilder<TerminalTranscriptChunk> builder)
    {
        builder.HasOne(c => c.TerminalSessionRecord)
            .WithMany(r => r.Chunks)
            .HasForeignKey(c => c.TerminalSessionRecordId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => new { c.TerminalSessionRecordId, c.Id });
    }
}
