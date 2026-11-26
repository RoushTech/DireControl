using DireControl.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

public class Message : IEntityTypeConfiguration<Message>
{
    public int Id { get; set; }
    public required string FromCallsign { get; set; }
    public required string ToCallsign { get; set; }
    public string Body { get; set; } = string.Empty;
    /// <summary>APRS message number.</summary>
    public string MessageId { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    /// <summary>
    /// Inbound only: how many copies of this message we have heard.  Values
    /// above 1 mean the sender retransmitted (usually because our ACK was lost).
    /// </summary>
    public int ReceiveCount { get; set; } = 1;
    /// <summary>Inbound only: UTC time the most recent copy was heard. Null until a retransmission arrives.</summary>
    public DateTime? LastReceivedAt { get; set; }
    /// <summary>
    /// Inbound only: the packet the first copy arrived in (source and via-path).
    /// Null for outbound messages, rows stored before this link existed, or once
    /// packet cleanup has purged the packet.
    /// </summary>
    public int? PacketId { get; set; }
    public Packet? Packet { get; set; }
    public bool IsRead { get; set; }
    public bool AckSent { get; set; }
    public bool ReplySent { get; set; }

    /// <summary>
    /// VIA path used when this message was originally sent.
    /// Null or empty means the message was sent direct with no digipeating.
    /// Stored so that retries use the same path as the initial send.
    /// </summary>
    public string? PathUsed { get; set; }

    // ── Retry fields ─────────────────────────────────────────────────────────
    /// <summary>Number of retransmissions performed beyond the initial send.</summary>
    public int RetryCount { get; set; }
    /// <summary>Maximum retransmissions before giving up.</summary>
    public int MaxRetries { get; set; } = 5;
    /// <summary>UTC time when the next automatic retry should fire. Null when not retrying.</summary>
    public DateTime? NextRetryAt { get; set; }
    public RetryState RetryState { get; set; } = RetryState.Pending;
    /// <summary>UTC time of the most recent transmission attempt.</summary>
    public DateTime? LastSentAt { get; set; }

    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.HasIndex(m => m.ReceivedAt);
        builder.Property(m => m.ReceiveCount).HasDefaultValue(1);
        builder.HasOne(m => m.Packet)
               .WithMany()
               .HasForeignKey(m => m.PacketId)
               .OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(m => new { m.RetryState, m.NextRetryAt });
    }
}
