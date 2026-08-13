using System.Collections.Concurrent;

namespace DireControl.Api.Services.Weather;

public readonly record struct LightningStrike(DateTime TimeUtc, double Latitude, double Longitude);

/// <summary>
/// In-memory rolling buffer of recent lightning strikes fed by <see cref="BlitzortungService"/>.
/// Strikes are enqueued in arrival order (approximately time order) and pruned past
/// <see cref="Retention"/>. Only the ingest loop calls <see cref="Add"/>.
/// </summary>
public sealed class LightningStrikeBuffer
{
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(60);

    // Safety cap on the persistence hand-off queue so a stalled writer can't grow it unbounded.
    private const int MaxPending = 200_000;

    private readonly ConcurrentQueue<LightningStrike> _strikes = new();
    private readonly ConcurrentQueue<LightningStrike> _pending = new();
    private int _pendingCount;

    public volatile bool IsConnected;

    private long _lastStrikeTicks;

    public DateTime? LastStrikeUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastStrikeTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    public int Count => _strikes.Count;

    public void Add(LightningStrike strike)
    {
        _strikes.Enqueue(strike);
        Interlocked.Exchange(ref _lastStrikeTicks, DateTime.UtcNow.Ticks);

        if (Interlocked.Increment(ref _pendingCount) <= MaxPending)
        {
            _pending.Enqueue(strike);
        }
        else
        {
            Interlocked.Decrement(ref _pendingCount);
        }

        var cutoff = DateTime.UtcNow - Retention;
        while (_strikes.TryPeek(out var oldest) && oldest.TimeUtc < cutoff)
            _strikes.TryDequeue(out _);
    }

    /// <summary>
    /// Removes up to <paramref name="max"/> strikes queued for persistence and returns them
    /// in arrival order. Called only by <see cref="LightningPersistenceService"/>.
    /// </summary>
    public List<LightningStrike> DrainPending(int max)
    {
        var drained = new List<LightningStrike>();
        while (drained.Count < max && _pending.TryDequeue(out var strike))
        {
            Interlocked.Decrement(ref _pendingCount);
            drained.Add(strike);
        }
        return drained;
    }

    /// <summary>
    /// Returns strikes newer than <paramref name="cutoffUtc"/> inside the bounding box,
    /// oldest first, keeping only the most recent <paramref name="limit"/>. Handles
    /// antimeridian-crossing boxes (minLon &gt; maxLon) and boxes spanning ≥ 360°.
    /// </summary>
    public List<LightningStrike> Query(
        double minLat, double maxLat, double minLon, double maxLon,
        DateTime cutoffUtc, int limit)
    {
        var wholeWorld = maxLon - minLon >= 360;
        if (!wholeWorld)
        {
            minLon = NormalizeLon(minLon);
            maxLon = NormalizeLon(maxLon);
        }

        var snapshot = _strikes.ToArray();
        var result = new List<LightningStrike>();

        // Newest strikes live at the tail; walk backwards so the limit keeps the most recent.
        for (var i = snapshot.Length - 1; i >= 0 && result.Count < limit; i--)
        {
            var s = snapshot[i];
            if (s.TimeUtc < cutoffUtc)
                continue;
            if (s.Latitude < minLat || s.Latitude > maxLat)
                continue;
            if (!wholeWorld)
            {
                var inLon = minLon <= maxLon
                    ? s.Longitude >= minLon && s.Longitude <= maxLon
                    : s.Longitude >= minLon || s.Longitude <= maxLon;
                if (!inLon)
                    continue;
            }
            result.Add(s);
        }

        result.Reverse();
        return result;
    }

    private static double NormalizeLon(double lon)
    {
        lon %= 360;
        if (lon >= 180) lon -= 360;
        if (lon < -180) lon += 360;
        return lon;
    }
}
