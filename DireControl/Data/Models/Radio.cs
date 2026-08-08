using DireControl.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DireControl.Data.Models;

public class Radio : IEntityTypeConfiguration<Radio>
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Name { get; set; }
    public required string Callsign { get; set; }
    public string? Ssid { get; set; }
    public string FullCallsign { get; set; } = string.Empty;

    /// <summary>
    /// KISS channel number — identifies this radio's traffic on the shared
    /// pipeline and routes outbound frames to the right modem instance.
    /// </summary>
    public int ChannelNumber { get; set; } = 0;

    public string? Notes { get; set; }
    public string? BeaconPath { get; set; }
    public string? BeaconSymbol { get; set; }
    public string? BeaconComment { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public int ExpectedIntervalSeconds { get; set; } = 600;

    /// <summary>
    /// Operating frequency in MHz, tracked manually — most packet radios
    /// (e.g. FT-8100R) have no CAT interface to read it from.  Radios with
    /// rigctld PTT get a live readout that supersedes this for display.
    /// </summary>
    public double? FrequencyMhz { get; set; }

    /// <summary>Operating mode, tracked manually — e.g. "FM", "Packet 1200".</summary>
    public string? Mode { get; set; }

    // ─── Sound modem (per-radio audio feed) ──────────────────────────────────

    /// <summary>Whether a native modem instance runs for this radio.</summary>
    public bool ModemEnabled { get; set; } = false;

    /// <summary>ALSA capture device for this radio's RX audio.</summary>
    public string ModemCaptureDevice { get; set; } = "default";

    /// <summary>ALSA playback device driving this radio's TX audio.</summary>
    public string ModemPlaybackDevice { get; set; } = "default";

    /// <summary>Whether this radio's modem transmits.</summary>
    public bool TxEnabled { get; set; } = false;

    /// <summary>TX audio amplitude as a percentage of full scale — the deviation control.</summary>
    public int TxAudioLevelPct { get; set; } = 80;

    /// <summary>Milliseconds of HDLC flags sent after keying PTT before the frame (TXDELAY).</summary>
    public int TxDelayMs { get; set; } = 300;

    /// <summary>Milliseconds of flags sent after the frame before unkeying (TXTail).</summary>
    public int TxTailMs { get; set; } = 50;

    /// <summary>p-persistence CSMA probability (0–255).</summary>
    public int TxPersistence { get; set; } = 63;

    /// <summary>CSMA slot time in milliseconds.</summary>
    public int TxSlotTimeMs { get; set; } = 100;

    // ─── PTT ─────────────────────────────────────────────────────────────────

    /// <summary>How this radio's transmitter is keyed.</summary>
    public PttMethod PttMethod { get; set; } = PttMethod.None;

    /// <summary>Serial port device for RTS/DTR PTT, e.g. /dev/ttyUSB0.</summary>
    public string? PttSerialPort { get; set; }

    /// <summary>Assert RTS to key (serial PTT).</summary>
    public bool PttSerialUseRts { get; set; } = true;

    /// <summary>Assert DTR to key (serial PTT).</summary>
    public bool PttSerialUseDtr { get; set; } = false;

    /// <summary>hidraw device for CM108 PTT, e.g. /dev/hidraw0.</summary>
    public string? PttHidDevice { get; set; }

    /// <summary>CM108 GPIO pin number (1–8); DigiRig and most interfaces use 3.</summary>
    public int PttHidPin { get; set; } = 3;

    /// <summary>GPIO chip number for Linux GPIO PTT (/dev/gpiochipN).</summary>
    public int PttGpioChip { get; set; } = 0;

    /// <summary>GPIO line (pin) number for Linux GPIO PTT.</summary>
    public int PttGpioLine { get; set; } = 0;

    /// <summary>Invert the GPIO PTT line (active low).</summary>
    public bool PttGpioActiveLow { get; set; } = false;

    /// <summary>Hamlib rigctld hostname for CAT PTT.</summary>
    public string PttRigctldHost { get; set; } = "localhost";

    /// <summary>Hamlib rigctld TCP port.</summary>
    public int PttRigctldPort { get; set; } = 4532;

    public ICollection<OwnBeacon> Beacons { get; set; } = [];

    public static string ComputeFullCallsign(string callsign, string? ssid) =>
        string.IsNullOrEmpty(ssid) ? callsign : $"{callsign}-{ssid}";

    public void Configure(EntityTypeBuilder<Radio> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasMaxLength(36);
        builder.Property(r => r.Name).IsRequired().HasMaxLength(100);
        builder.Property(r => r.Callsign).IsRequired().HasMaxLength(16);
        builder.Property(r => r.Ssid).HasMaxLength(4);
        builder.Property(r => r.FullCallsign).IsRequired().HasMaxLength(20);
        builder.Property(r => r.Notes).HasMaxLength(500);
        builder.Property(r => r.BeaconPath).HasMaxLength(100);
        builder.Property(r => r.BeaconSymbol).HasMaxLength(2);
        builder.Property(r => r.BeaconComment).HasMaxLength(256);
        builder.Property(r => r.Mode).HasMaxLength(16);
        builder.Property(r => r.ModemCaptureDevice).HasMaxLength(128);
        builder.Property(r => r.ModemPlaybackDevice).HasMaxLength(128);
        builder.Property(r => r.PttSerialPort).HasMaxLength(128);
        builder.Property(r => r.PttHidDevice).HasMaxLength(128);
        builder.Property(r => r.PttRigctldHost).HasMaxLength(256);
        builder.HasIndex(r => r.FullCallsign);
        builder.HasIndex(r => r.IsActive);
    }
}
