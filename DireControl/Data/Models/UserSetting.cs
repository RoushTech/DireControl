using System.ComponentModel.DataAnnotations.Schema;
using DireControl.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

/// <summary>
/// Single-row (Id = 1) user settings. All values live in one JSON document
/// column so adding a setting is just a new property with a default — no
/// migration. The entity exposes forwarding accessors so callers keep using
/// flat properties.
/// </summary>
public class UserSetting : IEntityTypeConfiguration<UserSetting>
{
    public int Id { get; set; }

    public UserSettingsDocument Settings { get; set; } = new();

    public void Configure(EntityTypeBuilder<UserSetting> builder)
    {
        builder.OwnsOne(s => s.Settings, b => b.ToJson());
    }

    // Forwarding accessors — call sites read/write flat properties.

    [NotMapped]
    public string OutboundPath { get => Settings.OutboundPath; set => Settings.OutboundPath = value; }
    [NotMapped]
    public bool AprsIsEnabled { get => Settings.AprsIsEnabled; set => Settings.AprsIsEnabled = value; }
    [NotMapped]
    public string AprsIsHost { get => Settings.AprsIsHost; set => Settings.AprsIsHost = value; }
    [NotMapped]
    public int? AprsIsPasscode { get => Settings.AprsIsPasscode; set => Settings.AprsIsPasscode = value; }
    [NotMapped]
    public string AprsIsFilter { get => Settings.AprsIsFilter; set => Settings.AprsIsFilter = value; }
    [NotMapped]
    public int DeduplicationWindowSeconds { get => Settings.DeduplicationWindowSeconds; set => Settings.DeduplicationWindowSeconds = value; }
    [NotMapped]
    public int PacketRetentionRfDays { get => Settings.PacketRetentionRfDays; set => Settings.PacketRetentionRfDays = value; }
    [NotMapped]
    public int PacketRetentionAprsIsDays { get => Settings.PacketRetentionAprsIsDays; set => Settings.PacketRetentionAprsIsDays = value; }
    [NotMapped]
    public int PacketRetentionOwnDays { get => Settings.PacketRetentionOwnDays; set => Settings.PacketRetentionOwnDays = value; }
    [NotMapped]
    public string? OpenWeatherMapApiKey { get => Settings.OpenWeatherMapApiKey; set => Settings.OpenWeatherMapApiKey = value; }
    [NotMapped]
    public string? TomorrowIoApiKey { get => Settings.TomorrowIoApiKey; set => Settings.TomorrowIoApiKey = value; }
    [NotMapped]
    public RadarProvider RadarProvider { get => Settings.RadarProvider; set => Settings.RadarProvider = value; }
    [NotMapped]
    public string? RainViewerProApiKey { get => Settings.RainViewerProApiKey; set => Settings.RainViewerProApiKey = value; }
    [NotMapped]
    public string? OurCallsign { get => Settings.OurCallsign; set => Settings.OurCallsign = value; }
    [NotMapped]
    public double? HomeLat { get => Settings.HomeLat; set => Settings.HomeLat = value; }
    [NotMapped]
    public double? HomeLon { get => Settings.HomeLon; set => Settings.HomeLon = value; }
    [NotMapped]
    public string? QrzUsername { get => Settings.QrzUsername; set => Settings.QrzUsername = value; }
    [NotMapped]
    public string? QrzPassword { get => Settings.QrzPassword; set => Settings.QrzPassword = value; }
    [NotMapped]
    public int? MaxRetryAttempts { get => Settings.MaxRetryAttempts; set => Settings.MaxRetryAttempts = value; }
    [NotMapped]
    public int? InitialRetryDelaySeconds { get => Settings.InitialRetryDelaySeconds; set => Settings.InitialRetryDelaySeconds = value; }
    [NotMapped]
    public double? DatabaseCleanupIntervalHours { get => Settings.DatabaseCleanupIntervalHours; set => Settings.DatabaseCleanupIntervalHours = value; }
    [NotMapped]
    public bool? VacuumOnCleanup { get => Settings.VacuumOnCleanup; set => Settings.VacuumOnCleanup = value; }
}

/// <summary>
/// The JSON-stored settings document. Add new settings here (with a default);
/// missing fields on existing rows deserialize to the property default.
/// Nullable members mean "not set in the UI" — the effective value falls back
/// to the appsettings default (see StationSettingsProvider).
/// </summary>
public class UserSettingsDocument
{
    /// <summary>
    /// VIA path added to all outbound messages.
    /// Empty string means transmit direct with no digipeating.
    /// </summary>
    public string OutboundPath { get; set; } = "WIDE1-1,WIDE2-1";

    // APRS-IS

    /// <summary>Whether the DireControl APRS-IS client is enabled.</summary>
    public bool AprsIsEnabled { get; set; } = false;

    /// <summary>APRS-IS server hostname. Defaults to the global round-robin.</summary>
    public string AprsIsHost { get; set; } = "rotate.aprs2.net";

    /// <summary>
    /// APRS-IS passcode override. When null, the passcode is auto-computed
    /// from OurCallsign using the standard algorithm.
    /// </summary>
    public int? AprsIsPasscode { get; set; }

    /// <summary>APRS-IS server-side filter string, e.g. "r/35.18/-85.08/500 t/m".</summary>
    public string AprsIsFilter { get; set; } = "r/39.0/-98.0/500 t/m";

    /// <summary>
    /// Number of seconds within which a duplicate packet (same callsign + info field)
    /// from either RF or APRS-IS is suppressed.
    /// </summary>
    public int DeduplicationWindowSeconds { get; set; } = 60;

    // Packet retention (database cleanup)

    /// <summary>
    /// Days of RF-received packet history to keep. 0 (the default) means keep
    /// forever — RF traffic is your own station's heard traffic and is small.
    /// </summary>
    public int PacketRetentionRfDays { get; set; } = 0;

    /// <summary>
    /// Days of APRS-IS packet history to keep before pruning. APRS-IS is the
    /// high-volume internet feed, so this defaults to a short window. 0 = keep forever.
    /// </summary>
    public int PacketRetentionAprsIsDays { get; set; } = 14;

    /// <summary>Days of own-transmitted packet history to keep. 0 = keep forever.</summary>
    public int PacketRetentionOwnDays { get; set; } = 0;

    // Weather overlay API keys

    /// <summary>OpenWeatherMap API key used for the wind tile overlay.</summary>
    public string? OpenWeatherMapApiKey { get; set; }

    /// <summary>Tomorrow.io API key used for the lightning tile overlay.</summary>
    public string? TomorrowIoApiKey { get; set; }

    /// <summary>Radar tile provider. Defaults to IEM NEXRAD (free, US coverage).</summary>
    public RadarProvider RadarProvider { get; set; } = RadarProvider.IemNexrad;

    /// <summary>RainViewer Pro API key. Only used when RadarProvider is RainViewerPro.</summary>
    public string? RainViewerProApiKey { get; set; }

    // Station identity & policy overrides (null = use the appsettings default)

    /// <summary>Primary station callsign (with optional SSID) — APRS-IS login and message identity.</summary>
    public string? OurCallsign { get; set; }

    /// <summary>Home station latitude (decimal degrees).</summary>
    public double? HomeLat { get; set; }

    /// <summary>Home station longitude (decimal degrees).</summary>
    public double? HomeLon { get; set; }

    /// <summary>QRZ.com username for callsign lookups (optional; HamDB is always tried first).</summary>
    public string? QrzUsername { get; set; }

    /// <summary>QRZ.com password for callsign lookups.</summary>
    public string? QrzPassword { get; set; }

    /// <summary>Maximum retransmission attempts for unacknowledged outbound messages.</summary>
    public int? MaxRetryAttempts { get; set; }

    /// <summary>Delay in seconds before the first retry; doubles each attempt.</summary>
    public int? InitialRetryDelaySeconds { get; set; }

    /// <summary>Hours between automatic database cleanup runs. 0 disables.</summary>
    public double? DatabaseCleanupIntervalHours { get; set; }

    /// <summary>Whether cleanup runs VACUUM after pruning.</summary>
    public bool? VacuumOnCleanup { get; set; }
}
