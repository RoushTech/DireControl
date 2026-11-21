using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// The packets-per-hour split behind the Network page's stacked RF / APRS-IS chart, against a
/// real (in-memory) SQLite database so the grouped query has to translate.
/// </summary>
[TestFixture]
public sealed class PacketsPerHourBySourceTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 18, 30, 0, DateTimeKind.Utc);

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

        foreach (var callsign in new[] { "K4TUX-9", "N0CALL-10" })
        {
            _db.Stations.Add(new Station
            {
                Callsign = callsign,
                FirstSeen = Now,
                LastSeen = Now,
                Symbol = "/-",
            });
        }
        _db.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void AddPacket(PacketSource source, DateTime receivedAt, string callsign = "K4TUX-9")
    {
        _db.Packets.Add(new Packet
        {
            StationCallsign = callsign,
            ReceivedAt = receivedAt,
            RawPacket = $"{callsign}>APRS:>test",
            Source = source,
        });
    }

    private Task<(int[] All, int[] Rf, int[] Is)> RunAsync() =>
        StatisticsAggregationService.ComputePacketsPerHourAsync(_db, Now, CancellationToken.None);

    [Test]
    public async Task SplitsRfAndAprsIsIntoSeparateSeries()
    {
        // Current hour (index 23): 2 RF, 3 APRS-IS.
        AddPacket(PacketSource.Rf, Now.AddMinutes(-5));
        AddPacket(PacketSource.Rf, Now.AddMinutes(-10));
        AddPacket(PacketSource.AprsIs, Now.AddMinutes(-1));
        AddPacket(PacketSource.AprsIs, Now.AddMinutes(-2));
        AddPacket(PacketSource.AprsIs, Now.AddMinutes(-3));

        // Five hours ago (index 18): RF has collapsed, APRS-IS carries on.
        AddPacket(PacketSource.AprsIs, Now.AddHours(-5).AddMinutes(-1));
        AddPacket(PacketSource.AprsIs, Now.AddHours(-5).AddMinutes(-2));

        // Twenty-three hours ago (index 0): RF only.
        AddPacket(PacketSource.Rf, Now.AddHours(-23).AddMinutes(-15));

        await _db.SaveChangesAsync();

        var (all, rf, aprsIs) = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rf, Has.Length.EqualTo(24));
            Assert.That(aprsIs, Has.Length.EqualTo(24));

            Assert.That(rf[23], Is.EqualTo(2));
            Assert.That(aprsIs[23], Is.EqualTo(3));
            Assert.That(all[23], Is.EqualTo(5));

            Assert.That(rf[18], Is.EqualTo(0));
            Assert.That(aprsIs[18], Is.EqualTo(2));

            Assert.That(rf[0], Is.EqualTo(1));
            Assert.That(aprsIs[0], Is.EqualTo(0));

            Assert.That(rf.Sum(), Is.EqualTo(3));
            Assert.That(aprsIs.Sum(), Is.EqualTo(5));
        });
    }

    [Test]
    public async Task OwnTransmissionsCountInNeitherSourceSeries()
    {
        AddPacket(PacketSource.Own, Now.AddMinutes(-4), "N0CALL-10");
        AddPacket(PacketSource.Own, Now.AddHours(-2), "N0CALL-10");
        AddPacket(PacketSource.Rf, Now.AddMinutes(-6));
        await _db.SaveChangesAsync();

        var (all, rf, aprsIs) = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rf.Sum(), Is.EqualTo(1));
            Assert.That(aprsIs.Sum(), Is.EqualTo(0));
            Assert.That(rf[23], Is.EqualTo(1));
            Assert.That(rf[21], Is.EqualTo(0));

            // The legacy all-sources total still includes them.
            Assert.That(all[23], Is.EqualTo(2));
            Assert.That(all[21], Is.EqualTo(1));
        });
    }

    [Test]
    public async Task IgnoresPacketsOlderThan24Hours()
    {
        AddPacket(PacketSource.Rf, Now.AddHours(-25));
        AddPacket(PacketSource.AprsIs, Now.AddHours(-30));
        await _db.SaveChangesAsync();

        var (all, rf, aprsIs) = await RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(all.Sum(), Is.EqualTo(0));
            Assert.That(rf.Sum(), Is.EqualTo(0));
            Assert.That(aprsIs.Sum(), Is.EqualTo(0));
        });
    }
}
