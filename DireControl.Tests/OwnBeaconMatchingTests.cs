using DireControl.Enums;
using DireControl.PathParsing;
using Xunit;
using AprsPacketType = AprsSharp.AprsParser.PacketType;
using DbPacket = DireControl.Data.Models.Packet;
using DbRadio = DireControl.Data.Models.Radio;

namespace DireControl.Tests;

/// <summary>
/// Tests for <see cref="CallsignMatcher.FindMatchingRadio"/> — the radio-selection
/// logic used by the own-beacon detection pipeline.
///
/// The method has two distinct strategies depending on packet source:
///   RF      — match by KISS channel first, then verify callsign.
///   APRS-IS — KISS channel is meaningless (always 0 by default);
///              match by callsign alone.
/// </summary>
public class FindMatchingRadioTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private static DbPacket RfPacket(string callsign, int kissChannel = 0) => new()
    {
        StationCallsign = callsign,
        RawPacket       = $"{callsign}>APRS:!data",
        Source          = PacketSource.Rf,
        KissChannel     = kissChannel,
    };

    private static DbPacket AprsIsPacket(string callsign) => new()
    {
        StationCallsign = callsign,
        RawPacket       = $"{callsign}>APRS,qAR,N8DEU-7:!data",
        Source          = PacketSource.AprsIs,
        // KissChannel stays at 0 — int default, never set for APRS-IS packets
    };

    private static DbRadio MakeRadio(string callsign, string? ssid = null, int channel = 0) => new()
    {
        Name          = "Test",
        Callsign      = callsign,
        Ssid          = ssid,
        ChannelNumber = channel,
    };

    // ── RF source — KISS channel is the primary key ────────────────────────────

    [Fact]
    public void Rf_ChannelAndCallsignMatch_ReturnsRadio()
    {
        var radio  = MakeRadio("W3UWU", channel: 0);
        var packet = RfPacket("W3UWU", kissChannel: 0);

        Assert.Same(radio, CallsignMatcher.FindMatchingRadio(packet, [radio]));
    }

    [Fact]
    public void Rf_ChannelMatchesButWrongCallsign_ReturnsNull()
    {
        // Different station heard on the same KISS port — must not be treated as own beacon.
        var radio  = MakeRadio("W3UWU", channel: 0);
        var packet = RfPacket("K9OTHER", kissChannel: 0);

        Assert.Null(CallsignMatcher.FindMatchingRadio(packet, [radio]));
    }

    [Fact]
    public void Rf_WrongChannel_ReturnsNull()
    {
        // Radio is configured on channel 1; packet arrived on channel 0.
        var radio  = MakeRadio("W3UWU", channel: 1);
        var packet = RfPacket("W3UWU", kissChannel: 0);

        Assert.Null(CallsignMatcher.FindMatchingRadio(packet, [radio]));
    }

    [Fact]
    public void Rf_NonDefaultChannel_MatchingCallsign_ReturnsRadio()
    {
        // A radio on channel 1 must be found when a matching RF packet arrives on channel 1.
        var radio  = MakeRadio("W3UWU", channel: 1);
        var packet = RfPacket("W3UWU", kissChannel: 1);

        Assert.Same(radio, CallsignMatcher.FindMatchingRadio(packet, [radio]));
    }

    [Fact]
    public void Rf_TwoRadiosDifferentChannels_MatchesCorrectOne()
    {
        var radioA = MakeRadio("W3UWU",  channel: 0);
        var radioB = MakeRadio("KD4RFT", channel: 1);
        var packet = RfPacket("KD4RFT", kissChannel: 1);

        Assert.Same(radioB, CallsignMatcher.FindMatchingRadio(packet, [radioA, radioB]));
    }

    [Fact]
    public void Rf_Ssid0EquivalentToNoSsid_Matches()
    {
        // W3UWU-0 in a packet is the same station as W3UWU (no SSID).
        var radio  = MakeRadio("W3UWU", ssid: null, channel: 0);
        var packet = RfPacket("W3UWU-0", kissChannel: 0);

        Assert.Same(radio, CallsignMatcher.FindMatchingRadio(packet, [radio]));
    }

    [Fact]
    public void Rf_EmptyRadioList_ReturnsNull()
    {
        var packet = RfPacket("W3UWU", kissChannel: 0);

        Assert.Null(CallsignMatcher.FindMatchingRadio(packet, []));
    }

    // ── APRS-IS source — callsign only, channel is irrelevant ─────────────────

    [Fact]
    public void AprsIs_MatchingCallsign_ReturnsRadio()
    {
        var radio  = MakeRadio("W3UWU");
        var packet = AprsIsPacket("W3UWU");

        Assert.Same(radio, CallsignMatcher.FindMatchingRadio(packet, [radio]));
    }

    [Fact]
    public void AprsIs_NonMatchingCallsign_ReturnsNull()
    {
        var radio  = MakeRadio("W3UWU");
        var packet = AprsIsPacket("K9OTHER");

        Assert.Null(CallsignMatcher.FindMatchingRadio(packet, [radio]));
    }

    /// <summary>
    /// Regression test for the APRS-IS beacon-detection bug.
    ///
    /// APRS-IS packets always have <c>KissChannel = 0</c> (int default, never set).
    /// Before the fix, all sources used channel-based matching, so a radio on channel 1
    /// would never match an APRS-IS echo of its own beacon — the beacon panel only
    /// updated when a <em>digipeated</em> confirmation arrived (a separate code path
    /// that sent a SignalR broadcast regardless).
    /// </summary>
    [Fact]
    public void AprsIs_RadioOnNonZeroChannel_StillMatchesByCallsign()
    {
        var radio  = MakeRadio("W3UWU", channel: 1);  // non-zero channel
        var packet = AprsIsPacket("W3UWU");             // KissChannel = 0 (default)

        // Must match even though packet.KissChannel (0) != radio.ChannelNumber (1)
        Assert.Same(radio, CallsignMatcher.FindMatchingRadio(packet, [radio]));
    }

    [Fact]
    public void AprsIs_TwoRadios_MatchesCorrectOne()
    {
        var radioA = MakeRadio("W3UWU");
        var radioB = MakeRadio("KD4RFT");
        var packet = AprsIsPacket("KD4RFT");

        Assert.Same(radioB, CallsignMatcher.FindMatchingRadio(packet, [radioA, radioB]));
    }

    [Fact]
    public void AprsIs_Ssid0EquivalentToNoSsid_Matches()
    {
        var radio  = MakeRadio("W3UWU", ssid: null);
        var packet = AprsIsPacket("W3UWU-0");

        Assert.Same(radio, CallsignMatcher.FindMatchingRadio(packet, [radio]));
    }

    [Fact]
    public void AprsIs_EmptyRadioList_ReturnsNull()
    {
        var packet = AprsIsPacket("W3UWU");

        Assert.Null(CallsignMatcher.FindMatchingRadio(packet, []));
    }
}

/// <summary>
/// Verifies that the raw TNC2 strings for each APRS packet type DireControl supports
/// are correctly recognised by AprsSharp.AprsParser — confirming the format assumptions
/// baked into DireControl's packet-type mapping pipeline.
/// </summary>
public class AprsPacketTypeParsingTests
{
    private static AprsPacketType? Parse(string tnc2)
        => new AprsSharp.AprsParser.Packet(tnc2).InfoField?.Type;

    // ── Position packets ──────────────────────────────────────────────────────

    [Fact]
    public void Bang_PositionWithoutTimestampNoMessaging()
    {
        Assert.Equal(
            AprsPacketType.PositionWithoutTimestampNoMessaging,
            Parse("W1ABC>APRS:!3400.59NT08402.69W&PHG8140Suwanee, GA digi-gate."));
    }

    [Fact]
    public void Equals_PositionWithoutTimestampWithMessaging()
    {
        Assert.Equal(
            AprsPacketType.PositionWithoutTimestampWithMessaging,
            Parse("W1ABC>APRS:=3400.59NT08402.69W&PHG8140comment"));
    }

    [Fact]
    public void At_PositionWithTimestampWithMessaging()
    {
        // real-world packet from issue #14
        Assert.Equal(
            AprsPacketType.PositionWithTimestampWithMessaging,
            Parse("W4PFT-1>APRS:@020107z3422.75N/08313.65W#WX3in1Mini Updated 03-20-2023 U=14.2V"));
    }

    [Fact]
    public void Slash_PositionWithTimestampNoMessaging()
    {
        // real-world packet from issue #17
        Assert.Equal(
            AprsPacketType.PositionWithTimestampNoMessaging,
            Parse("KR4BRU-9>APRS:/020150z3504.35N/08511.40Wa065/000/A=000751Ramble-Ambulance"));
    }

    // ── Status ────────────────────────────────────────────────────────────────

    [Fact]
    public void GreaterThan_Status()
    {
        Assert.Equal(
            AprsPacketType.Status,
            Parse("W1ABC>APRS:>Net control for the Tuesday net"));
    }

    // ── Message ───────────────────────────────────────────────────────────────

    [Fact]
    public void Colon_InboxMessage()
    {
        // APRS message — addressee padded to 9 chars
        Assert.Equal(
            AprsPacketType.Message,
            Parse("W1ABC>APRS::W3UWU    :Hello world"));
    }

    [Fact]
    public void Colon_MessageAck()
    {
        Assert.Equal(
            AprsPacketType.Message,
            Parse("W1ABC>APRS::W3UWU    :ack001"));
    }

    // ── Weather ───────────────────────────────────────────────────────────────

    [Fact]
    public void Underscore_WeatherReport()
    {
        Assert.Equal(
            AprsPacketType.WeatherReport,
            Parse("W1ABC>APRS:_10090556c220s004g005t077r000p000P000h50b09900"));
    }

    // ── Object / Item ─────────────────────────────────────────────────────────

    [Fact]
    public void Semicolon_Object()
    {
        Assert.Equal(
            AprsPacketType.Object,
            Parse("W1ABC>APRS:;REPEATER *111111z3400.59NT08402.69W&146.940MHz"));
    }

    [Fact]
    public void CloseParen_Item()
    {
        Assert.Equal(
            AprsPacketType.Item,
            Parse("W1ABC>APRS:)ITEM !3400.59NT08402.69W&"));
    }
}
