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
    public bool IsRead { get; set; }
    public bool AckSent { get; set; }
    public bool ReplySent { get; set; }

    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.HasIndex(m => m.ReceivedAt);
    }
}
