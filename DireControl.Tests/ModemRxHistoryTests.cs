using DireControl.Api.Services;
using DireControl.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// The decode history behind the Diagnostics chart: counter deltas across modem
/// sessions, per-minute rows in real SQLite, and the bucketing the chart reads.
/// </summary>
[TestFixture]
public sealed class ModemRxHistoryTests
{
    private static readonly DateTime T0 = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
    private const string Radio = "radio-a";

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

    private Task Record(DateTime at, long decoded, long invalid, long repaired = 0, string radio = Radio) =>
        ModemRxHistoryService.RecordAsync(
            _db,
            new Dictionary<string, RxCounters> { [radio] = new(decoded, invalid, repaired) },
            at,
            CancellationToken.None);

    // ── Counter deltas ───────────────────────────────────────────────────────

    [Test]
    public void Tracker_FirstSample_CountsFromZero()
    {
        var tracker = new ModemRxCounterTracker();
        Assert.That(tracker.Advance(Radio, T0, new(5, 3, 1)), Is.EqualTo(new RxCounters(5, 3, 1)));
    }

    [Test]
    public void Tracker_SameSession_ReturnsTheChangeSinceTheLastSample()
    {
        var tracker = new ModemRxCounterTracker();
        tracker.Advance(Radio, T0, new(5, 3, 1));
        Assert.Multiple(() =>
        {
            Assert.That(tracker.Advance(Radio, T0, new(9, 10, 1)), Is.EqualTo(new RxCounters(4, 7, 0)));
            Assert.That(tracker.Advance(Radio, T0, new(9, 10, 1)).IsZero, Is.True);
        });
    }

    [Test]
    public void Tracker_NewSession_CountsFromZeroEvenWhenTotalsAreHigher()
    {
        var tracker = new ModemRxCounterTracker();
        tracker.Advance(Radio, T0, new(5, 3, 1));

        // The modem restarted and already passed the old totals before the next sample.
        var delta = tracker.Advance(Radio, T0.AddSeconds(10), new(8, 4, 2));
        Assert.That(delta, Is.EqualTo(new RxCounters(8, 4, 2)));
    }

    [Test]
    public void Tracker_TotalsWentBackwards_CountsFromZero()
    {
        var tracker = new ModemRxCounterTracker();
        tracker.Advance(Radio, T0, new(50, 30, 10));
        Assert.That(tracker.Advance(Radio, T0, new(2, 1, 0)), Is.EqualTo(new RxCounters(2, 1, 0)));
    }

    [Test]
    public void Tracker_KeepsRadiosApart()
    {
        var tracker = new ModemRxCounterTracker();
        tracker.Advance(Radio, T0, new(5, 3, 0));
        tracker.Advance("radio-b", T0, new(100, 0, 0));
        Assert.That(tracker.Advance(Radio, T0, new(6, 3, 0)), Is.EqualTo(new RxCounters(1, 0, 0)));
    }

    // ── Bucket sizing ────────────────────────────────────────────────────────

    [TestCase(1, 1)]
    [TestCase(6, 5)]
    [TestCase(24, 15)]
    [TestCase(168, 120)]
    [TestCase(720, 480)]
    public void BucketMinutesFor_KeepsTheRangeWithinTheBucketLimit(int hours, int expectedMinutes)
    {
        Assert.Multiple(() =>
        {
            Assert.That(ModemRxHistoryLogic.BucketMinutesFor(hours), Is.EqualTo(expectedMinutes));
            Assert.That(hours * 60 / expectedMinutes, Is.LessThanOrEqualTo(ModemRxHistoryLogic.MaxBuckets));
        });
    }

    [Test]
    public void Bucket_ReturnsEveryBucket_OldestFirst_EndingAtTheCurrentOne()
    {
        var now = T0.AddMinutes(7).AddSeconds(30);
        var result = ModemRxHistoryLogic.Bucket([], now, 6);

        Assert.Multiple(() =>
        {
            Assert.That(result.BucketMinutes, Is.EqualTo(5));
            Assert.That(result.Buckets, Has.Count.EqualTo(72));
            Assert.That(result.Buckets[^1].Start, Is.EqualTo(T0.AddMinutes(5)), "holds 12:07");
            Assert.That(result.Buckets[0].Start, Is.EqualTo(T0.AddMinutes(5).AddMinutes(-71 * 5)));
            Assert.That(result.Buckets.Sum(b => b.DecodedFrames + b.InvalidFrames), Is.Zero);
        });
    }

    [Test]
    public void Bucket_SumsMinutesIntoTheirBucket_AndIgnoresRowsOutsideTheRange()
    {
        var now = T0.AddMinutes(7);
        var result = ModemRxHistoryLogic.Bucket(
            [
                (T0.AddMinutes(5), 2, 10, 1),
                (T0.AddMinutes(7), 1, 4, 0),
                (T0.AddMinutes(4), 3, 0, 0),
                (T0.AddDays(-2), 99, 99, 99),
            ],
            now,
            6);

        Assert.Multiple(() =>
        {
            Assert.That(result.Buckets[^1].DecodedFrames, Is.EqualTo(3));
            Assert.That(result.Buckets[^1].InvalidFrames, Is.EqualTo(14));
            Assert.That(result.Buckets[^1].RepairedFrames, Is.EqualTo(1));
            Assert.That(result.Buckets[^2].DecodedFrames, Is.EqualTo(3));
            Assert.That(result.Buckets.Sum(b => b.InvalidFrames), Is.EqualTo(14));
        });
    }

    // ── Storage ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Record_AddsToTheSameMinutesRow_AndStartsANewRowNextMinute()
    {
        await Record(T0.AddSeconds(5), decoded: 2, invalid: 3);
        await Record(T0.AddSeconds(50), decoded: 1, invalid: 4, repaired: 1);
        await Record(T0.AddSeconds(65), decoded: 0, invalid: 6);

        var rows = await _db.ModemRxMinutes.AsNoTracking().OrderBy(r => r.Minute).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows[0].Minute, Is.EqualTo(T0));
            Assert.That(rows[0].DecodedFrames, Is.EqualTo(3));
            Assert.That(rows[0].InvalidFrames, Is.EqualTo(7));
            Assert.That(rows[0].RepairedFrames, Is.EqualTo(1));
            Assert.That(rows[1].Minute, Is.EqualTo(T0.AddMinutes(1)));
            Assert.That(rows[1].InvalidFrames, Is.EqualTo(6));
        });
    }

    [Test]
    public async Task Query_ReturnsOnlyTheRequestedRadio_BucketedOverTheRange()
    {
        await Record(T0.AddMinutes(-30), decoded: 4, invalid: 1);
        await Record(T0.AddMinutes(-3), decoded: 1, invalid: 9);
        await Record(T0.AddMinutes(-3), decoded: 50, invalid: 50, radio: "radio-b");
        await Record(T0.AddHours(-3), decoded: 7, invalid: 7);

        var result = await ModemRxHistoryService.QueryAsync(_db, Radio, 1, T0, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.BucketMinutes, Is.EqualTo(1));
            Assert.That(result.Buckets, Has.Count.EqualTo(60));
            Assert.That(result.Buckets[^1].Start, Is.EqualTo(T0));
            Assert.That(result.Buckets[^4].InvalidFrames, Is.EqualTo(9));
            Assert.That(result.Buckets.Sum(b => b.DecodedFrames), Is.EqualTo(5));
            Assert.That(result.Buckets.Sum(b => b.InvalidFrames), Is.EqualTo(10));
        });
    }

    [Test]
    public async Task Prune_DeletesOnlyRowsOlderThanTheLongestRange()
    {
        await Record(T0.AddDays(-40), decoded: 1, invalid: 1);
        await Record(T0.AddDays(-29), decoded: 1, invalid: 1);
        await Record(T0, decoded: 1, invalid: 1);

        var deleted = await ModemRxHistoryService.PruneAsync(_db, T0, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(deleted, Is.EqualTo(1));
            Assert.That(_db.ModemRxMinutes.Count(), Is.EqualTo(2));
        });
    }
}
