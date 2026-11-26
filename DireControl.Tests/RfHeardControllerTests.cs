using DireControl.Api.Controllers;
using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// The read endpoints that derive direct-RF reception live from packets rather than from the
/// daily archive. Run against real SQLite so the grouped queries have to translate — the
/// whole point of deriving live is that the grouping happens in the database.
/// </summary>
[TestFixture]
public sealed class RfHeardControllerTests
{
    private const string OurCallsign = "N0CALL-10";

    private SqliteConnection _connection = null!;
    private DireControlContext _db = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<DireControlContext>().UseSqlite(_connection).Options;
        _db = new DireControlContext(options);
        _db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // Home at the equator/prime meridian: one degree of latitude is ~111 km.
    private RfHeardController CreateController(double? homeLat = 0.0, double? homeLon = 0.0) =>
        new(_db, Options.Create(new DireControlOptions
        {
            OurCallsign = OurCallsign,
            HomeLat = homeLat,
            HomeLon = homeLon,
        }));

    private static T Body<T>(ActionResult<T> result) =>
        (T)((OkObjectResult)result.Result!).Value!;

    private void AddRadio(string name, string callsign, string? ssid, int channel)
    {
        _db.Radios.Add(new Radio
        {
            Name = name,
            Callsign = callsign,
            Ssid = ssid,
            FullCallsign = Radio.ComputeFullCallsign(callsign, ssid),
            ChannelNumber = channel,
        });
    }

    private void AddStation(string callsign, double? lat = null, double? lon = null)
    {
        _db.Stations.Add(new Station
        {
            Callsign = callsign,
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
            LastLat = lat,
            LastLon = lon,
            Symbol = "/-",
        });
    }

    private void AddPacket(
        string callsign, int channel, DateTime receivedAt,
        HeardVia heardVia = HeardVia.Direct, PacketSource source = PacketSource.Rf)
    {
        _db.Packets.Add(new Packet
        {
            StationCallsign = callsign,
            ReceivedAt = receivedAt,
            RawPacket = $"{callsign}>APRS:>test",
            Source = source,
            HeardVia = heardVia,
            KissChannel = channel,
        });
    }

    // =========================================================================
    // /stations — who has each radio heard direct
    // =========================================================================

    [Test]
    public async Task GetStations_GroupsPerRadioAndSpansAllPackets()
    {
        AddRadio("2m Ringo", "W3UWU", "1", channel: 0);
        AddRadio("70cm J-pole", "W3UWU", "2", channel: 1);
        AddStation("K4TUX-9");

        var first = DateTime.UtcNow.AddDays(-3);
        var last = DateTime.UtcNow.AddMinutes(-5);
        AddPacket("K4TUX-9", 0, first);
        AddPacket("K4TUX-9", 0, last);
        AddPacket("K4TUX-9", 1, last);
        await _db.SaveChangesAsync();

        var result = Body(await CreateController().GetStations());

        Assert.That(result, Has.Count.EqualTo(2), "one row per radio that heard it");

        var ringo = result.Single(r => r.ChannelNumber == 0);
        Assert.Multiple(() =>
        {
            Assert.That(ringo.RadioName, Is.EqualTo("2m Ringo"));
            Assert.That(ringo.DirectPacketCount, Is.EqualTo(2));
            Assert.That(ringo.FirstHeardDirect, Is.EqualTo(first).Within(TimeSpan.FromSeconds(1)));
            Assert.That(ringo.LastHeardDirect, Is.EqualTo(last).Within(TimeSpan.FromSeconds(1)));
            Assert.That(result.Single(r => r.ChannelNumber == 1).RadioName, Is.EqualTo("70cm J-pole"));
        });
    }

    [Test]
    public async Task GetStations_ExcludesDigipeatedInternetAndOwnTraffic()
    {
        AddRadio("2m Ringo", "W3UWU", "1", channel: 0);
        foreach (var call in new[] { "K4TUX-9", "W4ABC-1", "N4XYZ", "W3UWU-1", OurCallsign })
            AddStation(call);

        var when = DateTime.UtcNow.AddMinutes(-5);
        AddPacket("K4TUX-9", 0, when);                                        // counted
        AddPacket("W4ABC-1", 0, when, HeardVia.Digi);                         // digipeated
        AddPacket("N4XYZ", 0, when, HeardVia.IgateRf, PacketSource.AprsIs);   // internet
        AddPacket("W3UWU-1", 0, when);                                        // our radio
        AddPacket(OurCallsign, 0, when);                                      // our station
        await _db.SaveChangesAsync();

        var result = Body(await CreateController().GetStations());

        Assert.That(result.Select(r => r.Callsign), Is.EquivalentTo(new[] { "K4TUX-9" }));
    }

    [Test]
    public async Task GetStations_FiltersToOneRadio()
    {
        AddRadio("2m Ringo", "W3UWU", "1", channel: 0);
        AddRadio("70cm J-pole", "W3UWU", "2", channel: 1);
        AddStation("K4TUX-9");
        AddStation("W4ABC-1");

        var when = DateTime.UtcNow.AddMinutes(-5);
        AddPacket("K4TUX-9", 0, when);
        AddPacket("W4ABC-1", 1, when);
        await _db.SaveChangesAsync();

        var result = Body(await CreateController().GetStations(channel: 1));

        Assert.That(result.Select(r => r.Callsign), Is.EquivalentTo(new[] { "W4ABC-1" }));
    }

    [Test]
    public async Task GetStations_ReportsDistanceFromTheStationsLastKnownPosition()
    {
        AddRadio("2m Ringo", "W3UWU", "1", channel: 0);
        AddStation("K4TUX-9", lat: 1.0, lon: 0.0);
        AddStation("W4ABC-1");   // no position known

        var when = DateTime.UtcNow.AddMinutes(-5);
        AddPacket("K4TUX-9", 0, when);
        AddPacket("W4ABC-1", 0, when);
        await _db.SaveChangesAsync();

        var result = Body(await CreateController().GetStations());

        Assert.Multiple(() =>
        {
            Assert.That(result.Single(r => r.Callsign == "K4TUX-9").DistanceKm,
                        Is.EqualTo(111.0).Within(2.0));
            Assert.That(result.Single(r => r.Callsign == "W4ABC-1").DistanceKm, Is.Null);
        });
    }

    [Test]
    public async Task GetStations_ListsAStationLongSinceGoneQuiet()
    {
        // Reception history is about what the radio managed to hear, not about whether the
        // station is still active. Going stale never removes a station (StationExpiryService
        // only broadcasts staleness), so its details are still joined in.
        AddRadio("2m Ringo", "W3UWU", "1", channel: 0);
        AddStation("K4TUX-9", lat: 1.0, lon: 0.0);
        AddPacket("K4TUX-9", 0, DateTime.UtcNow.AddDays(-300));
        await _db.SaveChangesAsync();

        var result = Body(await CreateController().GetStations());

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Symbol, Is.EqualTo("/-"));
            Assert.That(result[0].DistanceKm, Is.EqualTo(111.0).Within(2.0));
        });
    }

    [Test]
    public async Task GetStations_UsesTheChannelNumberWhenNoRadioIsConfigured()
    {
        // History outlives radio rows; a deleted radio must not erase what it heard.
        AddStation("K4TUX-9");
        AddPacket("K4TUX-9", 3, DateTime.UtcNow.AddMinutes(-5));
        await _db.SaveChangesAsync();

        var result = Body(await CreateController().GetStations());

        Assert.Multiple(() =>
        {
            Assert.That(result[0].RadioName, Is.EqualTo("Channel 3"));
            Assert.That(result[0].RadioId, Is.Null);
        });
    }

    // =========================================================================
    // /summary — headline figures per radio
    // =========================================================================

    [Test]
    public async Task GetSummary_CountsEachStationOncePerWindow()
    {
        AddRadio("2m Ringo", "W3UWU", "1", channel: 0);
        AddStation("K4TUX-9");
        AddStation("W4ABC-1");

        // One station heard on many days must count once, not once per day.
        for (var d = 0; d < 5; d++)
            AddPacket("K4TUX-9", 0, DateTime.UtcNow.AddDays(-d));

        AddPacket("W4ABC-1", 0, DateTime.UtcNow.AddDays(-20));   // inside 30d, outside 7d
        await _db.SaveChangesAsync();

        var summary = Body(await CreateController().GetSummary()).Single();

        Assert.Multiple(() =>
        {
            Assert.That(summary.UniqueDirectToday, Is.EqualTo(1));
            Assert.That(summary.UniqueDirect7d, Is.EqualTo(1));
            Assert.That(summary.UniqueDirect30d, Is.EqualTo(2));
            Assert.That(summary.UniqueDirectAllTime, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task GetSummary_TakesFarthestEverFromTheArchiveSoPruningCannotShrinkIt()
    {
        AddRadio("2m Ringo", "W3UWU", "1", channel: 0);
        _db.RfHeardDailies.Add(new RfHeardDaily
        {
            ChannelNumber = 0,
            Day = RfHeardLogic.LocalDay(DateTime.UtcNow).AddDays(-200),
            UniqueDirectStations = 4,
            MaxDirectDistanceKm = 412.5,
        });
        await _db.SaveChangesAsync();

        // No packets remain from that day at all — they have been pruned.
        var summary = Body(await CreateController().GetSummary()).Single();

        Assert.That(summary.BestDistanceKm, Is.EqualTo(412.5).Within(0.01));
    }

    [Test]
    public async Task GetSummary_ListsAConfiguredRadioThatHasHeardNothing()
    {
        AddRadio("New antenna", "W3UWU", "3", channel: 2);
        await _db.SaveChangesAsync();

        var summary = Body(await CreateController().GetSummary()).Single();

        Assert.Multiple(() =>
        {
            Assert.That(summary.RadioName, Is.EqualTo("New antenna"));
            Assert.That(summary.UniqueDirectAllTime, Is.Zero);
            Assert.That(summary.LastHeardDirect, Is.Null);
        });
    }

    // =========================================================================
    // /daily — the archive, zero-filled for charting
    // =========================================================================

    [Test]
    public async Task GetDaily_ZeroFillsDaysWithNoReception()
    {
        AddRadio("2m Ringo", "W3UWU", "1", channel: 0);
        var today = RfHeardLogic.LocalDay(DateTime.UtcNow);
        _db.RfHeardDailies.Add(new RfHeardDaily
        {
            ChannelNumber = 0,
            Day = today.AddDays(-2),
            UniqueDirectStations = 7,
        });
        await _db.SaveChangesAsync();

        var result = Body(await CreateController().GetDaily(days: 5));

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Count.EqualTo(5), "one row per day, gaps included");
            Assert.That(result.Single(r => r.Day == today.AddDays(-2)).UniqueDirectStations,
                        Is.EqualTo(7));
            Assert.That(result.Single(r => r.Day == today).UniqueDirectStations, Is.Zero);
            Assert.That(result.Select(r => r.Day), Is.Ordered);
        });
    }

    [Test]
    public async Task GetDaily_ClampsAnAbsurdRange()
    {
        AddRadio("2m Ringo", "W3UWU", "1", channel: 0);
        await _db.SaveChangesAsync();

        var result = Body(await CreateController().GetDaily(days: 99999));

        Assert.That(result, Has.Count.EqualTo(400));
    }
}
