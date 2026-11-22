using DireControl.Api.Controllers.Models;

namespace DireControl.Api.Services;

/// <summary>A modem's running receive totals, or the change in them between two samples.</summary>
public readonly record struct RxCounters(long Decoded, long Invalid, long Repaired)
{
    public bool IsZero => Decoded == 0 && Invalid == 0 && Repaired == 0;

    public static RxCounters operator +(RxCounters a, RxCounters b) =>
        new(a.Decoded + b.Decoded, a.Invalid + b.Invalid, a.Repaired + b.Repaired);
}

/// <summary>
/// Turns each modem's running totals into the change since the previous sample.
/// The totals restart from zero with every capture session, so a new session
/// (or a total that went backwards) counts from zero instead of from the last
/// sample.
/// </summary>
public sealed class ModemRxCounterTracker
{
    private readonly Dictionary<string, (DateTime? SessionStart, RxCounters Totals)> _last = [];

    public RxCounters Advance(string radioId, DateTime? sessionStart, RxCounters totals)
    {
        var baseline = _last.TryGetValue(radioId, out var last)
            && last.SessionStart == sessionStart
            && totals.Decoded >= last.Totals.Decoded
            && totals.Invalid >= last.Totals.Invalid
            && totals.Repaired >= last.Totals.Repaired
                ? last.Totals
                : default;

        _last[radioId] = (sessionStart, totals);
        return new RxCounters(
            totals.Decoded - baseline.Decoded,
            totals.Invalid - baseline.Invalid,
            totals.Repaired - baseline.Repaired);
    }
}

/// <summary>Pure bucketing for the decode history chart.</summary>
public static class ModemRxHistoryLogic
{
    /// <summary>Longest range the chart can ask for; also how long the per-minute rows are kept.</summary>
    public const int MaxHours = 30 * 24;

    /// <summary>Most buckets a response carries, whatever the range.</summary>
    public const int MaxBuckets = 100;

    private static readonly int[] BucketSizes = [1, 5, 15, 30, 60, 120, 240, 480];

    public static DateTime TruncateToMinute(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    /// <summary>The finest bucket width that keeps a range of <paramref name="hours"/> within <see cref="MaxBuckets"/>.</summary>
    public static int BucketMinutesFor(int hours)
    {
        var totalMinutes = Math.Clamp(hours, 1, MaxHours) * 60;
        foreach (var size in BucketSizes)
        {
            if (totalMinutes / size <= MaxBuckets)
                return size;
        }
        return BucketSizes[^1];
    }

    /// <summary>
    /// Start of the oldest bucket for a range ending at <paramref name="utcNow"/>.
    /// Buckets sit on whole multiples of their width, so the newest one is the
    /// partly filled bucket holding the current minute.
    /// </summary>
    public static DateTime RangeStart(DateTime utcNow, int hours)
    {
        var size = BucketMinutesFor(hours);
        var count = Math.Clamp(hours, 1, MaxHours) * 60 / size;
        var newest = utcNow.Ticks / TimeSpan.TicksPerMinute / size;
        return new DateTime((newest - count + 1) * size * TimeSpan.TicksPerMinute, DateTimeKind.Utc);
    }

    /// <summary>
    /// Sums per-minute rows into the range's buckets.  Every bucket is returned,
    /// empty ones as zeros, so the chart's time axis has no gaps.
    /// </summary>
    public static ModemRxHistoryDto Bucket(
        IEnumerable<(DateTime Minute, int Decoded, int Invalid, int Repaired)> rows,
        DateTime utcNow,
        int hours)
    {
        var size = BucketMinutesFor(hours);
        var count = Math.Clamp(hours, 1, MaxHours) * 60 / size;
        var start = RangeStart(utcNow, hours);
        var bucketTicks = size * TimeSpan.TicksPerMinute;

        var decoded = new int[count];
        var invalid = new int[count];
        var repaired = new int[count];
        foreach (var row in rows)
        {
            var i = (row.Minute.Ticks - start.Ticks) / bucketTicks;
            if (row.Minute.Ticks < start.Ticks || i >= count)
                continue;
            decoded[i] += row.Decoded;
            invalid[i] += row.Invalid;
            repaired[i] += row.Repaired;
        }

        return new ModemRxHistoryDto
        {
            BucketMinutes = size,
            Buckets = Enumerable.Range(0, count)
                .Select(i => new ModemRxBucketDto
                {
                    Start = start.AddTicks(i * bucketTicks),
                    DecodedFrames = decoded[i],
                    InvalidFrames = invalid[i],
                    RepairedFrames = repaired[i],
                })
                .ToList(),
        };
    }
}
