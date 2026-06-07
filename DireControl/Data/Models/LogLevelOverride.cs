using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

/// <summary>
/// A runtime override of the log level for a given logger category, persisted so
/// it survives restarts. These are fed into the standard Microsoft.Extensions.Logging
/// filters (under "Logging:LogLevel:*") at the highest precedence, so an override
/// here wins over appsettings.json. Absence of a row means "inherit appsettings".
/// </summary>
public class LogLevelOverride : IEntityTypeConfiguration<LogLevelOverride>
{
    /// <summary>Logger category, e.g. "Default", "DireControl", "Microsoft.AspNetCore".</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>LogLevel name, e.g. "Debug", "Information", "Warning".</summary>
    public string Level { get; set; } = string.Empty;

    public void Configure(EntityTypeBuilder<LogLevelOverride> builder)
    {
        builder.HasKey(x => x.Category);
        builder.Property(x => x.Level).IsRequired();
    }
}
