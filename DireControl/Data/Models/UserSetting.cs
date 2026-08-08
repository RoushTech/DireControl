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

    // ─── Native sound modem ──────────────────────────────────────────────────

    /// <summary>Whether the native soundcard modem (DireWolf-free RX) is enabled.</summary>
    public bool ModemEnabled { get; set; } = false;

    /// <summary>ALSA capture device name, e.g. "default" or "plughw:CARD=Device".</summary>
    public string ModemCaptureDevice { get; set; } = "default";

    /// <summary>
    /// KISS channel number stamped on packets decoded by the native modem —
    /// lets RF statistics distinguish modem traffic from an external TNC when
    /// both backends run at once.
    /// </summary>
    public int ModemKissChannel { get; set; } = 0;

    // ─── Native sound modem — transmit ───────────────────────────────────────

    /// <summary>Whether the native modem transmits (beacons, messages, ACKs).</summary>
    public bool ModemTxEnabled { get; set; } = false;

    /// <summary>ALSA playback device driving the transmitter's audio input.</summary>
    public string ModemPlaybackDevice { get; set; } = "default";

    /// <summary>TX audio amplitude as a percentage of full scale — the deviation control.</summary>
    public int ModemTxAudioLevelPct { get; set; } = 80;

    /// <summary>Milliseconds of HDLC flags sent after keying PTT before the frame (TXDELAY).</summary>
    public int ModemTxDelayMs { get; set; } = 300;

    /// <summary>Milliseconds of flags sent after the frame before unkeying (TXTail).</summary>
    public int ModemTxTailMs { get; set; } = 50;

    /// <summary>
    /// p-persistence CSMA probability (0–255): after the channel clears,
    /// transmit with probability (P+1)/256 each slot, else wait a slot time.
    /// </summary>
    public int ModemPersistence { get; set; } = 63;

    /// <summary>CSMA slot time in milliseconds.</summary>
    public int ModemSlotTimeMs { get; set; } = 100;

    // ─── Native sound modem — PTT ────────────────────────────────────────────

    /// <summary>How the transmitter is keyed.</summary>
    public PttMethod ModemPttMethod { get; set; } = PttMethod.None;

    /// <summary>Serial port device for RTS/DTR PTT, e.g. /dev/ttyUSB0.</summary>
    public string? ModemPttSerialPort { get; set; }

    /// <summary>Assert RTS to key (serial PTT).</summary>
    public bool ModemPttSerialUseRts { get; set; } = true;

    /// <summary>Assert DTR to key (serial PTT).</summary>
    public bool ModemPttSerialUseDtr { get; set; } = false;

    /// <summary>hidraw device for CM108 PTT, e.g. /dev/hidraw0.</summary>
    public string? ModemPttHidDevice { get; set; }

    /// <summary>CM108 GPIO pin number (1–8); DigiRig and most interfaces use 3.</summary>
    public int ModemPttHidPin { get; set; } = 3;

    /// <summary>GPIO chip number for Linux GPIO PTT (/dev/gpiochipN).</summary>
    public int ModemPttGpioChip { get; set; } = 0;

    /// <summary>GPIO line (pin) number for Linux GPIO PTT.</summary>
    public int ModemPttGpioLine { get; set; } = 0;

    /// <summary>Invert the GPIO PTT line (active low).</summary>
    public bool ModemPttGpioActiveLow { get; set; } = false;

    /// <summary>Hamlib rigctld hostname for CAT PTT.</summary>
    public string ModemPttRigctldHost { get; set; } = "localhost";

    /// <summary>Hamlib rigctld TCP port.</summary>
    public int ModemPttRigctldPort { get; set; } = 4532;

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
