using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

public class UserSetting : IEntityTypeConfiguration<UserSetting>
{
    public int Id { get; set; }

    /// <summary>
    /// VIA path added to all outbound messages.
    /// Empty string means transmit direct with no digipeating.
    /// </summary>
    public string OutboundPath { get; set; } = "WIDE1-1,WIDE2-1";

    public void Configure(EntityTypeBuilder<UserSetting> builder)
    {
        builder.HasData(new UserSetting { Id = 1, OutboundPath = "WIDE1-1,WIDE2-1" });
    }
}
