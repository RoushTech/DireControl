using DireControl.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

/// <summary>
/// One piece of mail in the PMS (personal message system) served to inbound
/// AX.25 connections.  Deliberately separate from the APRS <see cref="Message"/>
/// table — PMS mail has subjects, bulletins, and kill semantics that APRS
/// messaging does not.  <see cref="Id"/> doubles as the message number shown
/// to RF users (<c>R 4</c>, <c>K 4</c>).
/// </summary>
public class PmsMessage : IEntityTypeConfiguration<PmsMessage>
{
    public int Id { get; set; }
    public PmsMessageType Type { get; set; } = PmsMessageType.Private;
    public required string FromCallsign { get; set; }

    /// <summary>Addressee callsign; bulletins use "ALL" or a category word.</summary>
    public required string ToCallsign { get; set; }

    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    /// <summary>Set when the *addressee* reads the message over RF — sysop web reads do not count.</summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>Killed (deleted) mail stays for the retention window, hidden from listings.</summary>
    public bool IsKilled { get; set; }
    public DateTime? KilledAt { get; set; }

    /// <summary>How the mail entered the system (RF session, local preview, sysop web).</summary>
    public TerminalSessionOrigin Origin { get; set; } = TerminalSessionOrigin.Unknown;

    public void Configure(EntityTypeBuilder<PmsMessage> builder)
    {
        builder.Property(m => m.FromCallsign).HasMaxLength(16);
        builder.Property(m => m.ToCallsign).HasMaxLength(16);
        builder.Property(m => m.Subject).HasMaxLength(120);
        builder.HasIndex(m => m.ToCallsign);
        builder.HasIndex(m => m.CreatedAt);
        builder.HasIndex(m => m.IsKilled);
    }
}
