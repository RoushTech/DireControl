using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// The <see cref="RfHeardDaily"/> archive, against a real (in-memory) SQLite database so the
/// aggregation's SQL actually has to translate.
///
/// Two properties matter throughout. Each radio is accounted for separately by KISS channel,
/// so two antennas hearing the same air can be compared. And every row is a pure function of
/// the packets in its day — never accumulated — so rebuilding a day always converges on the
/// same answer no matter how many times, or in what order, the pass runs.
/// </summary>
[TestFixture]
public sealed class RfHeardAggregationTests
{
    private const string OurCallsign = "N0CALL-10";

    // Home is at the equator/prime meridian so distances here are easy to reason about:
    // one degree of latitude is ~111 km.
    private const double HomeLat = 0.0;
    private const double HomeLon = 0.0;

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

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private Task RunAsync() =>
        RfHeardAggregationService.AggregateAsync(
            _db, OurCallsign, HomeLat, HomeLon, null, CancellationToken.None);

    private Task RunWithoutHomePositionAsync() =>
        RfHeardAggregationService.AggregateAsync(
            _db, OurCallsign, null, null, null, CancellationToken.None);

    /// <summary>A UTC instant that reliably falls inside the given local calendar day.</summary>
    private static DateTime MiddayUtcOfLocalDay(DateOnly day)
    {
        var (startUtc, _) = RfHeardLogic.LocalDayRangeUtc(day);
        return startUtc.AddHours(12);
    }

    private static DateOnly Today => RfHeardLogic.LocalDay(DateTime.UtcNow);

    private void AddRadio(string callsign, string? ssid, int channel)
    {
        _db.Radios.Add(new Radio
        {
            Name = $"Radio {channel}",
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
        string callsign,
        int channel,
        DateTime receivedAt,
        HeardVia heardVia = HeardVia.Direct,
        PacketSource source = PacketSource.Rf,
        double? lat = null,
        double? lon = null,
        string path = "")
    {
        _db.Packets.Add(new Packet
        {
            StationCallsign = callsign,
            ReceivedAt = receivedAt,
            RawPacket = $"{callsign}>APRS{(path.Length > 0 ? "," + path : "")}:>test",
            Source = source,
            HeardVia = heardVia,
            KissChannel = channel,
            Path = path,
            Latitude = lat,
            Longitude = lon,
        });
    }

    private Task<Dictionary<int, RfHeardDaily>> DailyByChannelAsync() =>
        _db.RfHeardDailies.AsNoTracking().ToDictionaryAsync(d => d.ChannelNumber);

    // =========================================================================
    // Per-radio attribution — the whole point of the feature
    // =========================================================================

    [Test]
    public async Task TwoRadiosHearingTheSameStation_AreAccountedForSeparately()
    {
        AddRadio("W3UWU", "1", channel: 0);
        AddRadio("W3UWU", "2", channel: 1);
        AddStation("K4TUX-9");

        var when = MiddayUtcOfLocalDay(Today);
        AddPacket("K4TUX-9", channel: 0, when);
        AddPacket("K4TUX-9", channel: 1, when.AddSeconds(1));
        await _db.SaveChangesAsync();

        await RunAsync();

        var daily = await _db.RfHeardDailies.AsNoTracking().ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(daily.Select(d => d.ChannelNumber), Is.EquivalentTo(new[] { 0, 1 }));
            Assert.That(daily, Has.All.Property(nameof(RfHeardDaily.UniqueDirectStations)).EqualTo(1));
        });
    }

    [Test]
    public async Task OneRadioHearingMore_ShowsAHigherUniqueCountThanTheOther()
    {
        AddRadio("W3UWU", "1", channel: 0);
        AddRadio("W3UWU", "2", channel: 1);

        var when = MiddayUtcOfLocalDay(Today);
        foreach (var call in new[] { "K4TUX-9", "W4ABC-1", "N4XYZ" })
        {
            AddStation(call);
            AddPacket(call, channel: 0, when);
        }

        // The second antenna only manages one of the three.
        AddPacket("K4TUX-9", channel: 1, when);
        await _db.SaveChangesAsync();

        await RunAsync();

        var daily = await DailyByChannelAsync();

        Assert.Multiple(() =>
        {
            Assert.That(daily[0].UniqueDirectStations, Is.EqualTo(3));
            Assert.That(daily[1].UniqueDirectStations, Is.EqualTo(1));
        });
    }

    // =========================================================================
    // What counts as "heard direct"
    // =========================================================================

    [Test]
    public async Task DigipeatedAndInternetPackets_AreNotCountedAsDirect()
    {
        AddRadio("W3UWU", "1", channel: 0);
        AddStation("K4TUX-9");
        AddStation("W4ABC-1");
        AddStation("N4XYZ");

        var when = MiddayUtcOfLocalDay(Today);
        AddPacket("K4TUX-9", 0, when, HeardVia.Direct);
        AddPacket("W4ABC-1", 0, when, HeardVia.Digi, path: "WE4MB-3*,WIDE2");
        AddPacket("N4XYZ", 0, when, HeardVia.IgateRf, PacketSource.AprsIs, path: "qAR,WE4MB");
        await _db.SaveChangesAsync();

        await RunAsync();

        var daily = await _db.RfHeardDailies.AsNoTracking().SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(daily.UniqueDirectStations, Is.EqualTo(1));
            Assert.That(daily.DirectPackets, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task OwnBeaconsDigipeatedBackToUs_AreNotCountedAsHeard()
    {
        AddRadio("W3UWU", "1", channel: 0);
        AddStation("W3UWU-1");
        AddStation(OurCallsign);
        AddStation("K4TUX-9");

        var when = MiddayUtcOfLocalDay(Today);
        AddPacket("W3UWU-1", 0, when);          // our own radio
        AddPacket(OurCallsign, 0, when);        // our configured station callsign
        AddPacket("K4TUX-9", 0, when);          // someone else
        await _db.SaveChangesAsync();

        await RunAsync();

        var daily = await _db.RfHeardDailies.AsNoTracking().SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(daily.UniqueDirectStations, Is.EqualTo(1));
            Assert.That(daily.DirectPackets, Is.EqualTo(1));
        });
    }

    // =========================================================================
    // Counters
    // =========================================================================

    [Test]
    public async Task NewDirectStations_CountsOnlyTheDayOfTheFirstEverDirectHear()
    {
        AddRadio("W3UWU", "1", channel: 0);
        AddStation("K4TUX-9");

        var yesterday = Today.AddDays(-1);
        AddPacket("K4TUX-9", 0, MiddayUtcOfLocalDay(yesterday));
        AddPacket("K4TUX-9", 0, MiddayUtcOfLocalDay(Today));
        await _db.SaveChangesAsync();

        await RunAsync();

        var daily = await _db.RfHeardDailies.AsNoTracking().ToDictionaryAsync(d => d.Day);

        Assert.Multiple(() =>
        {
            Assert.That(daily[yesterday].NewDirectStations, Is.EqualTo(1));
            Assert.That(daily[yesterday].UniqueDirectStations, Is.EqualTo(1));
            Assert.That(daily[Today].NewDirectStations, Is.Zero, "already known by then");
            Assert.That(daily[Today].UniqueDirectStations, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task NewDirectStations_IsPerRadio_SoASecondAntennaStartsFromScratch()
    {
        AddRadio("W3UWU", "1", channel: 0);
        AddRadio("W3UWU", "2", channel: 1);
        AddStation("K4TUX-9");

        // Long known to the first radio; the second hears it for the first time today.
        AddPacket("K4TUX-9", 0, MiddayUtcOfLocalDay(Today.AddDays(-3)));
        AddPacket("K4TUX-9", 0, MiddayUtcOfLocalDay(Today));
        AddPacket("K4TUX-9", 1, MiddayUtcOfLocalDay(Today));
        await _db.SaveChangesAsync();

        await RunAsync();

        var today = await _db.RfHeardDailies
            .AsNoTracking()
            .Where(d => d.Day == Today)
            .ToDictionaryAsync(d => d.ChannelNumber);

        Assert.Multiple(() =>
        {
            Assert.That(today[0].NewDirectStations, Is.Zero);
            Assert.That(today[1].NewDirectStations, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RunningRepeatedly_DoesNotDoubleCount()
    {
        AddRadio("W3UWU", "1", channel: 0);
        AddStation("K4TUX-9");

        var when = MiddayUtcOfLocalDay(Today);
        AddPacket("K4TUX-9", 0, when);
        AddPacket("K4TUX-9", 0, when.AddMinutes(1));
        await _db.SaveChangesAsync();

        await RunAsync();
        await RunAsync();
        await RunAsync();

        var daily = await _db.RfHeardDailies.AsNoTracking().SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(daily.DirectPackets, Is.EqualTo(2));
            Assert.That(daily.UniqueDirectStations, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task PacketsArrivingBetweenRuns_AreFoldedIn()
    {
        AddRadio("W3UWU", "1", channel: 0);
        AddStation("K4TUX-9");
        AddStation("W4ABC-1");

        var when = MiddayUtcOfLocalDay(Today);
        AddPacket("K4TUX-9", 0, when);
        await _db.SaveChangesAsync();
        await RunAsync();

        AddPacket("W4ABC-1", 0, when.AddMinutes(5));
        await _db.SaveChangesAsync();
        await RunAsync();

        var daily = await _db.RfHeardDailies.AsNoTracking().SingleAsync();

        Assert.That(daily.UniqueDirectStations, Is.EqualTo(2));
    }

    [Test]
    public async Task ADayIsRebuiltFromPackets_SoCorrectionsConverge()
    {
        AddRadio("W3UWU", "1", channel: 0);
        AddStation("K4TUX-9");
        AddStation("W4ABC-1");

        var when = MiddayUtcOfLocalDay(Today);
        AddPacket("K4TUX-9", 0, when);
        AddPacket("W4ABC-1", 0, when, HeardVia.Digi, path: "WE4MB-3*,WIDE2");
        await _db.SaveChangesAsync();
        await RunAsync();

        Assert.That((await _db.RfHeardDailies.AsNoTracking().SingleAsync()).UniqueDirectStations,
                    Is.EqualTo(1));

        // A reclassification (the RF-upgrade fix, say) turns the second packet direct.
        await _db.Packets
            .Where(p => p.StationCallsign == "W4ABC-1")
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.HeardVia, HeardVia.Direct));

        await RunAsync();

        Assert.That((await _db.RfHeardDailies.AsNoTracking().SingleAsync()).UniqueDirectStations,
                    Is.EqualTo(2));
    }

    [Test]
    public async Task ADayThatLosesItsPackets_IsZeroedRatherThanLeftStale()
    {
        // What pruning looks like to the archive: the row must stop claiming reception it can
        // no longer substantiate, instead of freezing at its last good value.
        AddRadio("W3UWU", "1", channel: 0);
        AddStation("K4TUX-9");

        AddPacket("K4TUX-9", 0, MiddayUtcOfLocalDay(Today));
        await _db.SaveChangesAsync();
        await RunAsync();

        await _db.Packets.ExecuteDeleteAsync();
        await RunAsync();

        var daily = await _db.RfHeardDailies.AsNoTracking().SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(daily.UniqueDirectStations, Is.Zero);
            Assert.That(daily.DirectPackets, Is.Zero);
        });
    }

    // =========================================================================
    // Distance — the metric that actually shows an antenna change
    // =========================================================================

    [Test]
    public async Task DistanceStats_UseTheFarthestCopyPerStation()
    {
        AddRadio("W3UWU", "1", channel: 0);
        AddStation("K4TUX-9");
        AddStation("W4ABC-1");
        AddStation("N4XYZ");

        var when = MiddayUtcOfLocalDay(Today);

        // ~111 km, ~222 km, ~333 km north of home. The nearest station beacons repeatedly:
        // it must still contribute exactly one value to the median.
        AddPacket("K4TUX-9", 0, when, lat: 1.0, lon: 0.0);
        AddPacket("K4TUX-9", 0, when.AddMinutes(1), lat: 1.0, lon: 0.0);
        AddPacket("K4TUX-9", 0, when.AddMinutes(2), lat: 1.0, lon: 0.0);
        AddPacket("W4ABC-1", 0, when, lat: 2.0, lon: 0.0);
        AddPacket("N4XYZ", 0, when, lat: 3.0, lon: 0.0);
        await _db.SaveChangesAsync();

        await RunAsync();

        var daily = await _db.RfHeardDailies.AsNoTracking().SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(daily.DirectStationsWithPosition, Is.EqualTo(3));
            Assert.That(daily.MaxDirectDistanceKm, Is.EqualTo(333.0).Within(2.0));
            Assert.That(daily.MedianDirectDistanceKm, Is.EqualTo(222.0).Within(2.0));
        });
    }

    [Test]
    public async Task DistanceFallsBackToTheStationsLastKnownPosition()
    {
        AddRadio("W3UWU", "1", channel: 0);
        AddStation("K4TUX-9", lat: 1.0, lon: 0.0);

        // A status report carries no position of its own.
        AddPacket("K4TUX-9", 0, MiddayUtcOfLocalDay(Today));
        await _db.SaveChangesAsync();

        await RunAsync();

        var daily = await _db.RfHeardDailies.AsNoTracking().SingleAsync();

        Assert.That(daily.MaxDirectDistanceKm, Is.EqualTo(111.0).Within(2.0));
    }

    [Test]
    public async Task StationsWithNoPosition_AreStillCountedButNotMeasured()
    {
        AddRadio("W3UWU", "1", channel: 0);
        AddStation("K4TUX-9");

        AddPacket("K4TUX-9", 0, MiddayUtcOfLocalDay(Today));
        await _db.SaveChangesAsync();

        await RunAsync();

        var daily = await _db.RfHeardDailies.AsNoTracking().SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(daily.UniqueDirectStations, Is.EqualTo(1));
            Assert.That(daily.DirectStationsWithPosition, Is.Zero);
            Assert.That(daily.MaxDirectDistanceKm, Is.Null);
            Assert.That(daily.MedianDirectDistanceKm, Is.Null);
        });
    }

    [Test]
    public async Task WithNoHomePosition_CountsStillWorkAndDistancesAreNull()
    {
        AddRadio("W3UWU", "1", channel: 0);
        AddStation("K4TUX-9", lat: 1.0, lon: 0.0);

        AddPacket("K4TUX-9", 0, MiddayUtcOfLocalDay(Today), lat: 1.0, lon: 0.0);
        await _db.SaveChangesAsync();

        await RunWithoutHomePositionAsync();

        var daily = await _db.RfHeardDailies.AsNoTracking().SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(daily.UniqueDirectStations, Is.EqualTo(1));
            Assert.That(daily.MaxDirectDistanceKm, Is.Null);
        });
    }

    // =========================================================================
    // Backfill — both of stored history and of packets stored before per-packet
    // classification existed
    // =========================================================================

    [Test]
    public async Task Backfill_ClassifiesOldPacketsAndThenCountsThem()
    {
        AddRadio("W3UWU", "1", channel: 0);
        AddStation("K4TUX-9");
        AddStation("W4ABC-1");

        var when = MiddayUtcOfLocalDay(Today);

        // Rows as stored before the HeardVia column existed: classification Unknown, path
        // intact. An unused WIDE alias is still a direct hear.
        AddPacket("K4TUX-9", 0, when, HeardVia.Unknown, path: "WIDE1-1,WIDE2-1");
        AddPacket("W4ABC-1", 0, when, HeardVia.Unknown, path: "WE4MB-3*,WIDE2");
        await _db.SaveChangesAsync();

        await RunAsync();

        var packets = await _db.Packets.AsNoTracking().ToDictionaryAsync(p => p.StationCallsign);
        var daily = await _db.RfHeardDailies.AsNoTracking().SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(packets["K4TUX-9"].HeardVia, Is.EqualTo(HeardVia.Direct));
            Assert.That(packets["W4ABC-1"].HeardVia, Is.EqualTo(HeardVia.Digi));
            Assert.That(daily.UniqueDirectStations, Is.EqualTo(1), "only the direct one counts");
        });
    }

    [Test]
    public async Task Backfill_LeavesNothingUnclassified_SoTheSweepStopsRescanning()
    {
        AddRadio("W3UWU", "1", channel: 0);
        AddStation("K4TUX-9");
        AddStation("W4ABC-1");

        // Server-injected q-constructs used to fall through as Unknown, which meant the sweep
        // re-read every one of them on every pass forever. Nothing may be left behind.
        var when = MiddayUtcOfLocalDay(Today);
        AddPacket("K4TUX-9", 0, when, HeardVia.Unknown, PacketSource.AprsIs, path: "qAS,WXSVR-AU");
        AddPacket("W4ABC-1", 0, when, HeardVia.Unknown, PacketSource.AprsIs, path: "qAU,SOMECALL");
        await _db.SaveChangesAsync();

        await RunAsync();

        var stillUnknown = await _db.Packets.CountAsync(p => p.HeardVia == HeardVia.Unknown);

        Assert.That(stillUnknown, Is.Zero);
    }

    [Test]
    public async Task ADayIsNotSummarisedWhileItsPacketsAreStillBeingClassified()
    {
        // Classification advances in packet-id order, so mid-drain the boundary sits inside
        // some day. Writing that day's row from its classified half would mark it done and
        // under-report it forever, so no archive row may appear until the sweep is finished.
        AddRadio("W3UWU", "1", channel: 0);
        AddStation("K4TUX-9");
        AddStation("W4ABC-1");

        var when = MiddayUtcOfLocalDay(Today);
        AddPacket("K4TUX-9", 0, when, HeardVia.Unknown);
        AddPacket("W4ABC-1", 0, when.AddMinutes(1), HeardVia.Unknown);
        await _db.SaveChangesAsync();

        // A pass that cannot finish classifying must not publish a partial day.
        await RfHeardAggregationService.AggregateAsync(
            _db, OurCallsign, HomeLat, HomeLon, null, CancellationToken.None,
            maxClassifyPerPass: 1);

        Assert.That(await _db.RfHeardDailies.CountAsync(), Is.Zero,
                    "no archive row while classification is mid-flight");

        // Once it drains, the day is summarised from the whole picture.
        await RunAsync();

        var daily = await _db.RfHeardDailies.AsNoTracking().SingleAsync();
        Assert.That(daily.UniqueDirectStations, Is.EqualTo(2));
    }

    [Test]
    public async Task Classification_StartsWithTheNewestPackets()
    {
        // A partly-classified table is still served by the live endpoints, so the half that
        // gets done first decides what the operator sees. Oldest-first showed a stale slice of
        // months-old traffic as though it were current; newest-first makes the recent window
        // right immediately and fills history in behind it.
        AddRadio("W3UWU", "1", channel: 0);
        AddStation("K4TUX-9");

        AddPacket("K4TUX-9", 0, MiddayUtcOfLocalDay(Today.AddDays(-100)), HeardVia.Unknown);
        AddPacket("K4TUX-9", 0, MiddayUtcOfLocalDay(Today), HeardVia.Unknown);
        await _db.SaveChangesAsync();

        await RfHeardAggregationService.AggregateAsync(
            _db, OurCallsign, HomeLat, HomeLon, null, CancellationToken.None,
            maxClassifyPerPass: 1);

        var packets = await _db.Packets.AsNoTracking().OrderBy(p => p.ReceivedAt).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(packets[0].HeardVia, Is.EqualTo(HeardVia.Unknown), "old packet waits");
            Assert.That(packets[1].HeardVia, Is.EqualTo(HeardVia.Direct), "recent packet done first");
        });
    }

    [Test]
    public async Task StoredHistoryOlderThanTheTrailingWindow_IsBackfilledOnFirstRun()
    {
        // The feature is meant to be useful immediately against an existing packet archive,
        // not only from the day it is switched on.
        AddRadio("W3UWU", "1", channel: 0);
        AddStation("K4TUX-9");

        var old = Today.AddDays(-40);
        AddPacket("K4TUX-9", 0, MiddayUtcOfLocalDay(old));
        await _db.SaveChangesAsync();

        await RunAsync();

        var daily = await _db.RfHeardDailies.AsNoTracking().SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(daily.Day, Is.EqualTo(old));
            Assert.That(daily.UniqueDirectStations, Is.EqualTo(1));
            Assert.That(daily.NewDirectStations, Is.EqualTo(1));
        });
    }
}
