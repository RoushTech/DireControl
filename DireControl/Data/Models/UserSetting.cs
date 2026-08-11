using DireControl.Enums;
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

    // ─── Station identity (overrides appsettings when set) ───────────────────

    /// <summary>Station callsign-SSID override. Null = use configuration.</summary>
    public string? OurCallsign { get; set; }

    /// <summary>Home latitude override, used for beaconing and range. Null = use configuration.</summary>
    public double? HomeLat { get; set; }

    /// <summary>Home longitude override. Null = use configuration.</summary>
    public double? HomeLon { get; set; }

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
    /// APRS-IS server-side filter string, e.g. "r/35.18/-85.08/500".
    /// Filter terms are OR'd by the server — an unscoped type filter like
    /// "t/m" matches matching packets from the entire network, not just the
    /// range given in another term.
    /// </summary>
    public string AprsIsFilter { get; set; } = "r/39.0/-98.0/500";

    /// <summary>
    /// Number of seconds within which a duplicate packet (same callsign + info field)
    /// from either RF or APRS-IS is suppressed.
    /// </summary>
    public int DeduplicationWindowSeconds { get; set; } = 60;

    // ─── Packet retention (database cleanup) ─────────────────────────────────

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

    // ─── Digipeater ──────────────────────────────────────────────────────────

    /// <summary>Whether the WIDEn-N digipeater is enabled.</summary>
    public bool DigipeaterEnabled { get; set; } = false;

    /// <summary>
    /// Largest WIDEn served; higher n (e.g. WIDE7-7 abuse) is trapped —
    /// digipeated once with callsign substitution, killing the path.
    /// </summary>
    public int DigipeaterMaxWideN { get; set; } = 2;

    /// <summary>Fill-in digi mode: serve only the first WIDE1-1 hop.</summary>
    public bool DigipeaterFillInOnly { get; set; } = false;

    // ─── KISS TCP server ─────────────────────────────────────────────────────

    /// <summary>Whether external apps may use DireControl as a KISS TNC over TCP.</summary>
    public bool KissServerEnabled { get; set; } = false;

    /// <summary>KISS server listen port (8001 is the DireWolf convention).</summary>
    public int KissServerPort { get; set; } = 8010;

    // ─── Connected-mode AX.25 (LAPB) ─────────────────────────────────────────

    /// <summary>Whether inbound AX.25 connections (SABM to our SSIDs) are answered.</summary>
    public bool ConnectedModeInboundEnabled { get; set; } = false;

    /// <summary>Maximum concurrent LAPB sessions (inbound + outbound).</summary>
    public int ConnectedModeMaxSessions { get; set; } = 10;

    /// <summary>Default maximum I-field bytes per frame for new sessions (16–256).</summary>
    public int ConnectedModeDefaultPaclen { get; set; } = 128;

    /// <summary>Default window size k for new sessions.</summary>
    public int ConnectedModeWindowSize { get; set; } = 4;

    /// <summary>Default T1 (retransmission timer) base in seconds, before path scaling.</summary>
    public int ConnectedModeT1Seconds { get; set; } = 3;

    /// <summary>Default N2 retry limit for new sessions.</summary>
    public int ConnectedModeRetries { get; set; } = 10;

    /// <summary>Try modulo-128 (SABME) first on outbound connections.</summary>
    public bool ConnectedModePreferMod128 { get; set; } = false;

    // ─── PMS (personal message system) ───────────────────────────────────────

    /// <summary>Whether the PMS mailbox answers inbound connections.</summary>
    public bool PmsEnabled { get; set; } = false;

    /// <summary>SSID the PMS listens on: base station callsign + this SSID.</summary>
    public int PmsSsid { get; set; } = 1;

    /// <summary>Banner shown after the SID line when a station connects.</summary>
    public string PmsBannerText { get; set; } = "Welcome to the DireControl mailbox. H for help.";

    /// <summary>Days to keep killed PMS mail before pruning. 0 = keep forever.</summary>
    public int PmsRetentionDays { get; set; } = 0;

    // ─── AGWPE TCP server ────────────────────────────────────────────────────

    /// <summary>Whether third-party packet apps may use DireControl via the AGWPE protocol.</summary>
    public bool AgwpeServerEnabled { get; set; } = false;

    /// <summary>AGWPE listen port (8000 is the AGWPE convention).</summary>
    public int AgwpeServerPort { get; set; } = 8000;

    /// <summary>
    /// AGWPE bind address.  The protocol has no real authentication, so this
    /// defaults to loopback; exposing it on 0.0.0.0 is a deliberate opt-in.
    /// </summary>
    public string AgwpeServerBindAddress { get; set; } = "127.0.0.1";

    // ─── Terminal ────────────────────────────────────────────────────────────

    /// <summary>Days of terminal session transcripts to keep. 0 = keep forever.</summary>
    public int TerminalTranscriptRetentionDays { get; set; } = 90;

    // ─── iGate ───────────────────────────────────────────────────────────────

    /// <summary>Forward RF-heard packets to APRS-IS with the qAR construct.</summary>
    public bool RfToIsGatingEnabled { get; set; } = false;

    /// <summary>
    /// Forward APRS-IS messages to RF (third-party format) when the addressee
    /// was recently heard on RF.
    /// </summary>
    public bool IsToRfGatingEnabled { get; set; } = false;

    /// <summary>VIA path for IS→RF transmissions. Empty = direct.</summary>
    public string IsToRfPath { get; set; } = string.Empty;

    /// <summary>How recently a station must have been heard on RF to receive IS→RF traffic.</summary>
    public int IsToRfRecentHeardMinutes { get; set; } = 30;

    // ─── External TNC (KISS TCP client, e.g. Direwolf) ───────────────────────

    /// <summary>
    /// Whether DireControl maintains a KISS/TCP client connection to an external
    /// TNC such as Direwolf.  Off by default — most stations use the native sound
    /// modem.  Enable only when a separate TNC is providing the RF link.
    /// </summary>
    public bool DirewolfEnabled { get; set; } = false;

    /// <summary>Hostname of the external KISS TNC.</summary>
    public string DirewolfHost { get; set; } = "localhost";

    /// <summary>TCP port of the external KISS TNC (8001 is the Direwolf convention).</summary>
    public int DirewolfPort { get; set; } = 8001;

    /// <summary>Seconds to wait before retrying a dropped external-TNC connection.</summary>
    public int DirewolfReconnectDelaySeconds { get; set; } = 5;

    // ─── Weather overlay API keys ─────────────────────────────────────────────

    /// <summary>OpenWeatherMap API key used for the wind tile overlay.</summary>
    public string? OpenWeatherMapApiKey { get; set; }

    /// <summary>Tomorrow.io API key used for the lightning tile overlay.</summary>
    public string? TomorrowIoApiKey { get; set; }

    /// <summary>Radar tile provider. Defaults to IEM NEXRAD (free, US coverage).</summary>
    public RadarProvider RadarProvider { get; set; } = RadarProvider.IemNexrad;

    /// <summary>RainViewer Pro API key. Only used when RadarProvider is RainViewerPro.</summary>
    public string? RainViewerProApiKey { get; set; }

    public void Configure(EntityTypeBuilder<UserSetting> builder)
    {
        builder.HasData(new UserSetting { Id = 1 });
    }
}
