using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

/// <summary>Per-connection LAPB parameter overrides saved with a preset. Nulls fall back to the defaults.</summary>
public sealed record TerminalLapbParams
{
    public bool? Mod128 { get; init; }
    public int? PacLen { get; init; }
    public int? WindowSize { get; init; }
    public int? MaxRetries { get; init; }
    public int? T1Seconds { get; init; }
}

/// <summary>
/// A saved terminal connection — recents and pinned favourites in one table.
/// Opening a session upserts the matching row (unique per destination +
/// channel + path); unpinned rows are trimmed oldest-first.
/// </summary>
public class TerminalPreset : IEntityTypeConfiguration<TerminalPreset>
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public required string RemoteCallsign { get; set; }
    public int Channel { get; set; }

    /// <summary>Comma-separated digipeater path; empty = direct.</summary>
    public string DigiPath { get; set; } = string.Empty;

    /// <summary>Local callsign override; null = station default.</summary>
    public string? LocalCallsign { get; set; }

    /// <summary>Saved LAPB overrides, stored as a JSON column.</summary>
    public TerminalLapbParams? Params { get; set; }

    public bool IsPinned { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public int UseCount { get; set; }

    public void Configure(EntityTypeBuilder<TerminalPreset> builder)
    {
        builder.Property(p => p.Name).HasMaxLength(60);
        builder.Property(p => p.RemoteCallsign).HasMaxLength(16);
        builder.Property(p => p.LocalCallsign).HasMaxLength(16);
        builder.Property(p => p.Params)
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<TerminalLapbParams>(v, (JsonSerializerOptions?)null));
        builder.HasIndex(p => new { p.RemoteCallsign, p.Channel, p.DigiPath }).IsUnique();
    }
}
