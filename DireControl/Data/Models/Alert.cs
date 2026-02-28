using System.Text.Json;
using DireControl.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

public class Alert : IEntityTypeConfiguration<Alert>
{
    public int Id { get; set; }
    public AlertType AlertType { get; set; }
    public required string Callsign { get; set; }
    public DateTime TriggeredAt { get; set; }
    public AlertDetail Detail { get; set; } = new();
    public bool IsAcknowledged { get; set; }

    public void Configure(EntityTypeBuilder<Alert> builder)
    {
        builder.HasIndex(a => a.TriggeredAt);
        builder.HasIndex(a => a.IsAcknowledged);
        builder.Property(a => a.Detail)
               .HasConversion(
                   v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                   v => JsonSerializer.Deserialize<AlertDetail>(v, (JsonSerializerOptions?)null) ?? new());
    }
}
