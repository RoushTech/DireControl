using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Decoder-level tests for the packet cases added after the PacketDecoder
/// extraction: telemetry parsing, status → station, and grid-square derivation.
/// </summary>
[TestFixture]
public class PacketDecoderTests
{
    private SqliteConnection _connection = null!;
    private DireControlContext _db = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<DireControlContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new DireControlContext(options);
        await _db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<Packet> DecodeAsync(string raw, string callsign, List<Radio>? activeRadios = null, List<MessageEffect>? effects = null)
    {
        var station = new Station
        {
            Callsign = callsign,
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
            Symbol = "/-",
        };
        _db.Stations.Add(station);
        await _db.SaveChangesAsync();

        var packet = new Packet
        {
            StationCallsign = callsign,
            ReceivedAt = DateTime.UtcNow,
            RawPacket = raw,
        };
        _db.Packets.Add(packet);

        await PacketDecoder.DecodeAsync(packet, _db, "W3UWU", effects ?? [], default, activeRadios: activeRadios);
        await _db.SaveChangesAsync();
        return packet;
    }

    private static Radio MakeRadio(string callsign, string? ssid = null, int channel = 0) => new()
    {
        Name = "Test",
        Callsign = callsign,
        Ssid = ssid,
        ChannelNumber = channel,
        IsActive = true,
    };

    // Any-radio message matching: messages addressed to an active radio's
    // callsign (not just the primary OurCallsign) land in the inbox, and the
    // ACK effect carries the addressed callsign so the ACK is sourced from it.

    [Test]
    public async Task Message_AddressedToActiveRadio_AddsInboxEntry_WithAddresseeOnEffect()
    {
        var effects = new List<MessageEffect>();
        await DecodeAsync(
            "W1ABC>APRS,WIDE2-1::W3UWU-7  :hello second radio{9",
            "W1ABC",
            activeRadios: [MakeRadio("W3UWU", "7")],
            effects: effects);

        var inbox = await _db.Messages.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(inbox.ToCallsign, Is.EqualTo("W3UWU-7"));
            Assert.That(inbox.FromCallsign, Is.EqualTo("W1ABC"));
            Assert.That(inbox.Body, Is.EqualTo("hello second radio"));
            Assert.That(effects, Has.Count.EqualTo(1));
            Assert.That(effects[0].IsNewInboxMessage, Is.True);
            Assert.That(effects[0].AddresseeCallsign, Is.EqualTo("W3UWU-7"));
        });
    }

    [Test]
    public async Task Message_AddressedToUnknownCallsign_IsIgnored()
    {
        var effects = new List<MessageEffect>();
        await DecodeAsync(
            "W1ABC>APRS,WIDE2-1::K9OTHER  :not for us{4",
            "W1ABC",
            activeRadios: [MakeRadio("W3UWU", "7")],
            effects: effects);

        Assert.Multiple(() =>
        {
            Assert.That(_db.Messages.Count(), Is.Zero);
            Assert.That(effects, Is.Empty);
        });
    }

    [Test]
    public async Task Message_AddressedToRadioWithZeroSsid_MatchesBareCallsign()
    {
        // "-0" SSID is equivalent to no SSID on both sides.
        var effects = new List<MessageEffect>();
        await DecodeAsync(
            "W1ABC>APRS,WIDE2-1::W4XYZ-0  :ssid zero{2",
            "W1ABC",
            activeRadios: [MakeRadio("W4XYZ")],
            effects: effects);

        Assert.That(await _db.Messages.CountAsync(), Is.EqualTo(1));
        Assert.That(effects[0].AddresseeCallsign, Is.EqualTo("W4XYZ-0"));
    }

    [Test]
    public async Task Message_AddressedToPrimaryCallsign_StillMatches_WithNoRadios()
    {
        var effects = new List<MessageEffect>();
        await DecodeAsync(
            "W1ABC>APRS,WIDE2-1::W3UWU    :to primary{7",
            "W1ABC",
            effects: effects);

        Assert.That(await _db.Messages.CountAsync(), Is.EqualTo(1));
        Assert.That(effects[0].IsNewInboxMessage, Is.True);
    }

    [Test]
    public async Task Telemetry_PopulatesTelemetryData()
    {
        var packet = await DecodeAsync(RealPacketData.Telemetry_T_Hash, "KN6RO-13");

        Assert.Multiple(() =>
        {
            Assert.That(packet.ParsedType, Is.EqualTo(PacketType.Telemetry));
            Assert.That(packet.TelemetryData, Is.Not.Null);
            Assert.That(packet.TelemetryData!.SequenceNumber, Is.EqualTo("132"));
            Assert.That(packet.TelemetryData.Analogs, Is.EqualTo(new double?[] { 179, 76, 21, 66, 0 }));
            Assert.That(packet.TelemetryData.Digitals, Is.EqualTo(new[]
            {
                false, false, false, false, false, false, false, false,
            }));
        });
    }

    [Test]
    public async Task Telemetry_RoundTripsThroughJsonColumn()
    {
        var packet = await DecodeAsync(RealPacketData.Telemetry_T_Hash, "KN6RO-13");

        // Re-read through a fresh context so the JSON converter round-trips.
        var optionsBuilder = new DbContextOptionsBuilder<DireControlContext>().UseSqlite(_connection);
        using var freshDb = new DireControlContext(optionsBuilder.Options);
        var reloaded = await freshDb.Packets.AsNoTracking().SingleAsync(p => p.Id == packet.Id);

        Assert.That(reloaded.TelemetryData, Is.Not.Null);
        Assert.That(reloaded.TelemetryData!.Analogs, Is.EqualTo(new double?[] { 179, 76, 21, 66, 0 }));
    }

    [Test]
    public async Task Status_SetsStationStatus()
    {
        var packet = await DecodeAsync("W1ABC>APRS,WIDE2-1:>Net check-in Tuesdays 1900", "W1ABC");

        var station = await _db.Stations.SingleAsync(s => s.Callsign == "W1ABC");
        Assert.Multiple(() =>
        {
            Assert.That(packet.ParsedType, Is.EqualTo(PacketType.Status));
            Assert.That(packet.Comment, Is.EqualTo("Net check-in Tuesdays 1900"));
            Assert.That(station.Status, Is.EqualTo("Net check-in Tuesdays 1900"));
        });
    }

    [Test]
    public async Task Position_SetsGridSquareOnPacketAndStation()
    {
        // 4903.50N/07201.75W → 49.0583, -72.0292 → FN39xb
        var packet = await DecodeAsync("W1ABC>APRS,WIDE2-1:=4903.50N/07201.75W-Test", "W1ABC");

        var station = await _db.Stations.SingleAsync(s => s.Callsign == "W1ABC");
        Assert.Multiple(() =>
        {
            Assert.That(packet.GridSquare, Is.Not.Null);
            Assert.That(packet.GridSquare, Does.StartWith("FN39"));
            Assert.That(station.GridSquare, Is.EqualTo(packet.GridSquare));
        });
    }

    // Comment-text altitude (/A=nnnnnn, feet) — APRS101 convention, parsed from
    // the comment because AprsSharp exposes no altitude property.

    [Test]
    public async Task Position_WithCommentAltitude_SetsStationAltitude()
    {
        // Real packet: ambulance beacon ending in "/A=000726".
        await DecodeAsync(RealPacketData.Position_SlashPrefix_TimestampZ, "KR4BRU-9");

        var station = await _db.Stations.SingleAsync(s => s.Callsign == "KR4BRU-9");
        Assert.That(station.LastAltitude, Is.EqualTo(726));
    }

    [Test]
    public async Task Position_WithoutAltitude_LeavesStationAltitudeNull()
    {
        await DecodeAsync("W1ABC>APRS,WIDE2-1:=4903.50N/07201.75W-Test", "W1ABC");

        var station = await _db.Stations.SingleAsync(s => s.Callsign == "W1ABC");
        Assert.That(station.LastAltitude, Is.Null);
    }

    [TestCase("/A=000726", 726)]
    [TestCase("019/000/A=000726Ramble-Ambulance", 726)]
    [TestCase("/A=-00050", -50)]
    [TestCase(null, null)]
    [TestCase("", null)]
    [TestCase("no altitude here", null)]
    public void ParseAltitudeFeet_ParsesCommentAltitude(string? comment, double? expected)
    {
        Assert.That(PacketDecoder.ParseAltitudeFeet(comment), Is.EqualTo(expected));
    }

    [Test]
    public async Task NonPositionPacket_LeavesGridSquareNull()
    {
        var packet = await DecodeAsync("W1ABC>APRS:>status only", "W1ABC");

        Assert.That(packet.GridSquare, Is.Null);
    }

    [Test]
    public async Task Reprocess_RederivesPacketGridSquare_ButNotStation()
    {
        var station = new Station
        {
            Callsign = "W2DEF",
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
            Symbol = "/-",
            GridSquare = "AA00aa",
        };
        _db.Stations.Add(station);
        var packet = new Packet
        {
            StationCallsign = "W2DEF",
            ReceivedAt = DateTime.UtcNow,
            RawPacket = "W2DEF>APRS,WIDE2-1:=4903.50N/07201.75W-Test",
        };
        _db.Packets.Add(packet);
        await _db.SaveChangesAsync();

        await PacketDecoder.DecodeAsync(packet, _db, "W3UWU", [], default, reprocess: true);

        Assert.Multiple(() =>
        {
            Assert.That(packet.GridSquare, Does.StartWith("FN39"));
            Assert.That(station.GridSquare, Is.EqualTo("AA00aa"), "reprocess must not mutate live station state");
        });
    }
}
