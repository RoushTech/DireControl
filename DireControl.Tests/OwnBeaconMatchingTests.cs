using DireControl.Enums;
using DireControl.PathParsing;
using NUnit.Framework;
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
[TestFixture]
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

    [Test]
    public void Rf_ChannelAndCallsignMatch_ReturnsRadio()
    {
        var radio  = MakeRadio("W3UWU", channel: 0);
        var packet = RfPacket("W3UWU", kissChannel: 0);

        Assert.That(CallsignMatcher.FindMatchingRadio(packet, [radio]), Is.SameAs(radio));
    }

    [Test]
    public void Rf_ChannelMatchesButWrongCallsign_ReturnsNull()
    {
        // Different station heard on the same KISS port — must not be treated as own beacon.
        var radio  = MakeRadio("W3UWU", channel: 0);
        var packet = RfPacket("K9OTHER", kissChannel: 0);

        Assert.That(CallsignMatcher.FindMatchingRadio(packet, [radio]), Is.Null);
    }

    [Test]
    public void Rf_WrongChannel_ReturnsNull()
    {
        // Radio is configured on channel 1; packet arrived on channel 0.
        var radio  = MakeRadio("W3UWU", channel: 1);
        var packet = RfPacket("W3UWU", kissChannel: 0);

        Assert.That(CallsignMatcher.FindMatchingRadio(packet, [radio]), Is.Null);
    }

    [Test]
    public void Rf_NonDefaultChannel_MatchingCallsign_ReturnsRadio()
    {
        // A radio on channel 1 must be found when a matching RF packet arrives on channel 1.
        var radio  = MakeRadio("W3UWU", channel: 1);
        var packet = RfPacket("W3UWU", kissChannel: 1);

        Assert.That(CallsignMatcher.FindMatchingRadio(packet, [radio]), Is.SameAs(radio));
    }

    [Test]
    public void Rf_TwoRadiosDifferentChannels_MatchesCorrectOne()
    {
        var radioA = MakeRadio("W3UWU",  channel: 0);
        var radioB = MakeRadio("KD4RFT", channel: 1);
        var packet = RfPacket("KD4RFT", kissChannel: 1);

        Assert.That(CallsignMatcher.FindMatchingRadio(packet, [radioA, radioB]), Is.SameAs(radioB));
    }

    [Test]
    public void Rf_Ssid0EquivalentToNoSsid_Matches()
    {
        // W3UWU-0 in a packet is the same station as W3UWU (no SSID).
        var radio  = MakeRadio("W3UWU", ssid: null, channel: 0);
        var packet = RfPacket("W3UWU-0", kissChannel: 0);

        Assert.That(CallsignMatcher.FindMatchingRadio(packet, [radio]), Is.SameAs(radio));
    }

    [Test]
    public void Rf_EmptyRadioList_ReturnsNull()
    {
        var packet = RfPacket("W3UWU", kissChannel: 0);

        Assert.That(CallsignMatcher.FindMatchingRadio(packet, []), Is.Null);
    }

    // ── APRS-IS source — callsign only, channel is irrelevant ─────────────────

    [Test]
    public void AprsIs_MatchingCallsign_ReturnsRadio()
    {
        var radio  = MakeRadio("W3UWU");
        var packet = AprsIsPacket("W3UWU");

        Assert.That(CallsignMatcher.FindMatchingRadio(packet, [radio]), Is.SameAs(radio));
    }

    [Test]
    public void AprsIs_NonMatchingCallsign_ReturnsNull()
    {
        var radio  = MakeRadio("W3UWU");
        var packet = AprsIsPacket("K9OTHER");

        Assert.That(CallsignMatcher.FindMatchingRadio(packet, [radio]), Is.Null);
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
    [Test]
    public void AprsIs_RadioOnNonZeroChannel_StillMatchesByCallsign()
    {
        var radio  = MakeRadio("W3UWU", channel: 1);  // non-zero channel
        var packet = AprsIsPacket("W3UWU");             // KissChannel = 0 (default)

        // Must match even though packet.KissChannel (0) != radio.ChannelNumber (1)
        Assert.That(CallsignMatcher.FindMatchingRadio(packet, [radio]), Is.SameAs(radio));
    }

    [Test]
    public void AprsIs_TwoRadios_MatchesCorrectOne()
    {
        var radioA = MakeRadio("W3UWU");
        var radioB = MakeRadio("KD4RFT");
        var packet = AprsIsPacket("KD4RFT");

        Assert.That(CallsignMatcher.FindMatchingRadio(packet, [radioA, radioB]), Is.SameAs(radioB));
    }

    [Test]
    public void AprsIs_Ssid0EquivalentToNoSsid_Matches()
    {
        var radio  = MakeRadio("W3UWU", ssid: null);
        var packet = AprsIsPacket("W3UWU-0");

        Assert.That(CallsignMatcher.FindMatchingRadio(packet, [radio]), Is.SameAs(radio));
    }

    [Test]
    public void AprsIs_EmptyRadioList_ReturnsNull()
    {
        var packet = AprsIsPacket("W3UWU");

        Assert.That(CallsignMatcher.FindMatchingRadio(packet, []), Is.Null);
    }
}

/// <summary>
/// Verifies that the raw TNC2 strings for each APRS packet type DireControl supports
/// are correctly recognised by AprsSharp.AprsParser — confirming the format assumptions
/// baked into DireControl's packet-type mapping pipeline.
/// </summary>
[TestFixture]
public class AprsPacketTypeParsingTests
{
    private static AprsPacketType? Parse(string tnc2)
        => new AprsSharp.AprsParser.Packet(tnc2).InfoField?.Type;

    // ── Position packets ──────────────────────────────────────────────────────

    [Test]
    public void Bang_PositionWithoutTimestampNoMessaging()
    {
        Assert.That(
            Parse("W1ABC>APRS:!3400.59NT08402.69W&PHG8140Suwanee, GA digi-gate."),
            Is.EqualTo(AprsPacketType.PositionWithoutTimestampNoMessaging));
    }

    [Test]
    public void Equals_PositionWithoutTimestampWithMessaging()
    {
        Assert.That(
            Parse("W1ABC>APRS:=3400.59NT08402.69W&PHG8140comment"),
            Is.EqualTo(AprsPacketType.PositionWithoutTimestampWithMessaging));
    }

    [Test]
    public void At_PositionWithTimestampWithMessaging()
    {
        // real-world packet from issue #14
        Assert.That(
            Parse("W4PFT-1>APRS:@020107z3422.75N/08313.65W#WX3in1Mini Updated 03-20-2023 U=14.2V"),
            Is.EqualTo(AprsPacketType.PositionWithTimestampWithMessaging));
    }

    [Test]
    public void Slash_PositionWithTimestampNoMessaging()
    {
        // real-world packet from issue #17
        Assert.That(
            Parse("KR4BRU-9>APRS:/020150z3504.35N/08511.40Wa065/000/A=000751Ramble-Ambulance"),
            Is.EqualTo(AprsPacketType.PositionWithTimestampNoMessaging));
    }

    // ── Status ────────────────────────────────────────────────────────────────

    [Test]
    public void GreaterThan_Status()
    {
        Assert.That(
            Parse("W1ABC>APRS:>Net control for the Tuesday net"),
            Is.EqualTo(AprsPacketType.Status));
    }

    // ── Message ───────────────────────────────────────────────────────────────

    [Test]
    public void Colon_InboxMessage()
    {
        // APRS message — addressee padded to 9 chars
        Assert.That(
            Parse("W1ABC>APRS::W3UWU    :Hello world"),
            Is.EqualTo(AprsPacketType.Message));
    }

    [Test]
    public void Colon_MessageAck()
    {
        Assert.That(
            Parse("W1ABC>APRS::W3UWU    :ack001"),
            Is.EqualTo(AprsPacketType.Message));
    }

    // ── Weather ───────────────────────────────────────────────────────────────

    [Test]
    public void Underscore_WeatherReport()
    {
        Assert.That(
            Parse("W1ABC>APRS:_10090556c220s004g005t077r000p000P000h50b09900"),
            Is.EqualTo(AprsPacketType.WeatherReport));
    }

    // ── Object / Item ─────────────────────────────────────────────────────────

    [Test]
    public void Semicolon_Object()
    {
        Assert.That(
            Parse("W1ABC>APRS:;REPEATER *111111z3400.59NT08402.69W&146.940MHz"),
            Is.EqualTo(AprsPacketType.Object));
    }

    [Test]
    public void CloseParen_Item()
    {
        Assert.That(
            Parse("W1ABC>APRS:)ITEM !3400.59NT08402.69W&"),
            Is.EqualTo(AprsPacketType.Item));
    }
}
