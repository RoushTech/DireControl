using DireControl.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

/// <summary>
/// Transcript header — one row per terminal-visible AX.25 session, created at
/// session start and finalised on close.  The byte stream itself lives in
/// <see cref="TerminalTranscriptChunk"/> rows.
/// </summary>
public class TerminalSessionRecord : IEntityTypeConfiguration<TerminalSessionRecord>
{
    public int Id { get; set; }

    /// <summary>The live session's GUID, for correlating UI state with history.</summary>
    public required string SessionId { get; set; }

    public TerminalSessionOrigin Origin { get; set; }
    public int Channel { get; set; }
    public required string LocalCallsign { get; set; }
    public required string RemoteCallsign { get; set; }

    /// <summary>Comma-separated digipeater path; empty = direct.</summary>
    public string DigiPath { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? EndReason { get; set; }
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }

    public ICollection<TerminalTranscriptChunk> Chunks { get; set; } = [];

    public void Configure(EntityTypeBuilder<TerminalSessionRecord> builder)
    {
        builder.Property(r => r.SessionId).HasMaxLength(32);
        builder.Property(r => r.LocalCallsign).HasMaxLength(16);
        builder.Property(r => r.RemoteCallsign).HasMaxLength(16);
        builder.HasIndex(r => r.SessionId);
        builder.HasIndex(r => r.StartedAt);
    }
}
