using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

/// <summary>
/// An operator-defined terminal macro button.  The payload is stored base64
/// so control characters and non-printable bytes survive.
/// </summary>
public class TerminalMacro : IEntityTypeConfiguration<TerminalMacro>
{
    public int Id { get; set; }
    public required string Label { get; set; }
    public int SortOrder { get; set; }
    public string PayloadBase64 { get; set; } = string.Empty;

    /// <summary>Append a CR after the payload (the packet line terminator).</summary>
    public bool AppendCr { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public void Configure(EntityTypeBuilder<TerminalMacro> builder)
    {
        builder.Property(m => m.Label).HasMaxLength(40);
        builder.HasIndex(m => m.SortOrder);
    }
}
