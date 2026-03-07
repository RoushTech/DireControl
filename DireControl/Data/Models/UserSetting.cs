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

    // ─── APRS-IS settings ────────────────────────────────────────────────────

    /// <summary>Whether the DireControl APRS-IS client is enabled.</summary>
    public bool AprsIsEnabled { get; set; } = false;

    /// <summary>APRS-IS server hostname. Defaults to the global round-robin.</summary>
    public string AprsIsHost { get; set; } = "rotate.aprs2.net";

    /// <summary>APRS-IS filtered server port.</summary>
    public int AprsIsPort { get; set; } = 14580;

    /// <summary>
    /// APRS-IS passcode override. When null, the passcode is auto-computed
    /// from OurCallsign using the standard algorithm.
    /// </summary>
    public int? AprsIsPasscode { get; set; }

    /// <summary>
    /// APRS-IS server-side filter string, e.g. "r/35.18/-85.08/500 t/m".
    /// </summary>
    public string AprsIsFilter { get; set; } = "r/39.0/-98.0/500 t/m";

    /// <summary>
    /// Number of seconds within which a duplicate packet (same callsign + info field)
    /// from either RF or APRS-IS is suppressed.
    /// </summary>
    public int DeduplicationWindowSeconds { get; set; } = 60;

    // ─── Weather overlay API keys ─────────────────────────────────────────────

    /// <summary>OpenWeatherMap API key used for the wind tile overlay.</summary>
    public string? OpenWeatherMapApiKey { get; set; }

    /// <summary>Tomorrow.io API key used for the lightning tile overlay.</summary>
    public string? TomorrowIoApiKey { get; set; }

    public void Configure(EntityTypeBuilder<UserSetting> builder)
    {
        builder.HasData(new UserSetting { Id = 1 });
    }
}
