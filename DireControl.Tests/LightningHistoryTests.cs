using System;
using System.Linq;
using DireControl.Api.Services.Weather;
using DireControl.Data;
using DireControl.Data.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Tests for the persisted lightning-strike pipeline: the buffer's persistence hand-off
/// queue and the SQL-translatable history query filters used by the history endpoint.
/// </summary>
[TestFixture]
public sealed class LightningHistoryTests
{
    // ── LightningStrikeBuffer.DrainPending ─────────────────────────────────

    [Test]
    public void DrainPending_ReturnsStrikesInArrivalOrder_AndEmptiesQueue()
    {
        var buffer = new LightningStrikeBuffer();
        buffer.Add(new LightningStrike(DateTime.UtcNow.AddMinutes(-2), 10, 10));
        buffer.Add(new LightningStrike(DateTime.UtcNow.AddMinutes(-1), 20, 20));

        var drained = buffer.DrainPending(100);
        Assert.That(drained.Select(s => s.Latitude), Is.EqualTo(new[] { 10.0, 20.0 }));
        Assert.That(buffer.DrainPending(100), Is.Empty);
    }

    [Test]
    public void DrainPending_RespectsMax_AndKeepsRemainderForNextDrain()
    {
        var buffer = new LightningStrikeBuffer();
        for (var i = 0; i < 5; i++)
            buffer.Add(new LightningStrike(DateTime.UtcNow, i, i));

        Assert.That(buffer.DrainPending(3), Has.Count.EqualTo(3));
        Assert.That(buffer.DrainPending(3), Has.Count.EqualTo(2));
    }

    [Test]
    public void DrainPending_IsIndependentOfDisplayBufferPruning()
    {
        var buffer = new LightningStrikeBuffer();
        // Older than the display retention — pruned from the display buffer immediately,
        // but must still reach the persistence queue.
        buffer.Add(new LightningStrike(DateTime.UtcNow - LightningStrikeBuffer.Retention - TimeSpan.FromMinutes(5), 1, 1));
        buffer.Add(new LightningStrike(DateTime.UtcNow, 2, 2));

        Assert.That(buffer.Count, Is.EqualTo(1));
        Assert.That(buffer.DrainPending(100), Has.Count.EqualTo(2));
    }

    // ── LightningHistoryQuery — executed against in-memory SQLite ──────────

    private static DireControlContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<DireControlContext>().UseSqlite(connection).Options;
        var db = new DireControlContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static LightningStrikeRecord Strike(DateTime timeUtc, double lat, double lon)
        => new() { TimeUtc = timeUtc, Latitude = lat, Longitude = lon };

    [Test]
    public void ApplyWindow_FiltersToInclusiveTimeRange()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = CreateDb(connection);

        var t0 = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);
        db.LightningStrikes.AddRange(
            Strike(t0.AddMinutes(-30), 1, 1),
            Strike(t0.AddMinutes(-10), 2, 2),
            Strike(t0, 3, 3),
            Strike(t0.AddMinutes(10), 4, 4));
        db.SaveChanges();

        var result = db.LightningStrikes
            .ApplyWindow(t0.AddMinutes(-10), t0)
            .OrderBy(s => s.TimeUtc)
            .ToList();

        Assert.That(result.Select(s => s.Latitude), Is.EqualTo(new[] { 2.0, 3.0 }));
    }

    [Test]
    public void ApplyBounds_FiltersSimpleBoundingBox()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = CreateDb(connection);

        var now = DateTime.UtcNow;
        db.LightningStrikes.AddRange(
            Strike(now, 35, -85),   // inside
            Strike(now, 55, -85),   // lat outside
            Strike(now, 35, -110)); // lon outside
        db.SaveChanges();

        var result = db.LightningStrikes
            .ApplyBounds(30, 40, -90, -80)
            .ToList();

        Assert.That(result.Single().Longitude, Is.EqualTo(-85));
    }

    [Test]
    public void ApplyBounds_HandlesAntimeridianCrossingBox()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = CreateDb(connection);

        var now = DateTime.UtcNow;
        db.LightningStrikes.AddRange(
            Strike(now, 0, 175),    // inside (west of the antimeridian)
            Strike(now, 0, -175),   // inside (east of the antimeridian)
            Strike(now, 0, 0));     // outside
        db.SaveChanges();

        var result = db.LightningStrikes
            .ApplyBounds(-10, 10, 170, -170)
            .ToList();

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(s => s.Longitude), Is.EquivalentTo(new[] { 175.0, -175.0 }));
    }

    [Test]
    public void ApplyBounds_NormalizesLeafletUnwrappedLongitudes()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = CreateDb(connection);

        var now = DateTime.UtcNow;
        db.LightningStrikes.AddRange(
            Strike(now, 0, -85),
            Strike(now, 0, 100));
        db.SaveChanges();

        // Leaflet can report bounds like [270, 280] after wrapping around the world;
        // that normalises to [-90, -80].
        var result = db.LightningStrikes
            .ApplyBounds(-10, 10, 270, 280)
            .ToList();

        Assert.That(result.Single().Longitude, Is.EqualTo(-85));
    }

    [Test]
    public void ApplyBounds_WholeWorldSpanReturnsEverything()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = CreateDb(connection);

        var now = DateTime.UtcNow;
        db.LightningStrikes.AddRange(
            Strike(now, 0, -179),
            Strike(now, 0, 179));
        db.SaveChanges();

        var result = db.LightningStrikes
            .ApplyBounds(-90, 90, -200, 200)
            .ToList();

        Assert.That(result, Has.Count.EqualTo(2));
    }
}
