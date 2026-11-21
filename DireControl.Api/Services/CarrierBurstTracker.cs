namespace DireControl.Api.Services;

/// <summary>
/// Freshness facts about one modem's receive side, written by the capture
/// thread and read by status snapshots on any thread: when audio last arrived
/// (so a frozen level meter can be told from a quiet channel), when the current
/// carrier-detect burst began, and how many preambles it has produced.  A long
/// carrier with zero preambles is noise or open squelch, not a transmission.
/// </summary>
public sealed class CarrierBurstTracker
{
    private long _levelUpdatedAtTicks;
    private long _carrierSinceTicks;
    private long _preamblesAtCarrierStart;

    // Carrier-detect duty over the last minute: 60 one-second buckets of
    // (samples with carrier, samples) — written by the capture thread only.
    private const int DutyBuckets = 60;
    private readonly Lock _dutyLock = new();
    private readonly long[] _carrierSamples = new long[DutyBuckets];
    private readonly long[] _totalSamples = new long[DutyBuckets];
    private long _bucketSecond = -1;

    /// <summary>Called once per capture block with the block's carrier state and the running preamble total.</summary>
    public void OnBlock(bool carrier, long preambleTotal, DateTime utcNow, int samples = 0)
    {
        Interlocked.Exchange(ref _levelUpdatedAtTicks, utcNow.Ticks);
        if (samples > 0)
            AddDuty(carrier, samples, utcNow);
        var since = Interlocked.Read(ref _carrierSinceTicks);
        if (carrier && since == 0)
        {
            Interlocked.Exchange(ref _preamblesAtCarrierStart, preambleTotal);
            Interlocked.Exchange(ref _carrierSinceTicks, utcNow.Ticks);
        }
        else if (!carrier && since != 0)
        {
            Interlocked.Exchange(ref _carrierSinceTicks, 0);
        }
    }

    private void AddDuty(bool carrier, int samples, DateTime utcNow)
    {
        var second = utcNow.Ticks / TimeSpan.TicksPerSecond;
        lock (_dutyLock)
        {
            if (_bucketSecond < 0 || second - _bucketSecond >= DutyBuckets)
            {
                Array.Clear(_carrierSamples);
                Array.Clear(_totalSamples);
            }
            else
            {
                // Clear the buckets skipped since the last block.
                for (var s = _bucketSecond + 1; s <= second; s++)
                {
                    var i = (int)(s % DutyBuckets);
                    _carrierSamples[i] = 0;
                    _totalSamples[i] = 0;
                }
            }
            if (second > _bucketSecond)
                _bucketSecond = second;
            var b = (int)(second % DutyBuckets);
            _totalSamples[b] += samples;
            if (carrier)
                _carrierSamples[b] += samples;
        }
    }

    /// <summary>
    /// Fraction (0–1) of the last minute's audio during which carrier detect was
    /// held; null before any audio. A busy channel sits around 0.1–0.4; near 1
    /// with no decodes is open squelch or noise.
    /// </summary>
    public double? CarrierDutyLastMinute(DateTime utcNow)
    {
        var second = utcNow.Ticks / TimeSpan.TicksPerSecond;
        lock (_dutyLock)
        {
            if (_bucketSecond < 0 || second - _bucketSecond >= DutyBuckets)
                return null;
            long carrier = 0, total = 0;
            for (var s = second - DutyBuckets + 1; s <= _bucketSecond; s++)
            {
                if (s < 0)
                    continue;
                var i = (int)(s % DutyBuckets);
                carrier += _carrierSamples[i];
                total += _totalSamples[i];
            }
            return total == 0 ? null : (double)carrier / total;
        }
    }

    /// <summary>Forget everything — a new capture session is starting.</summary>
    public void Reset()
    {
        lock (_dutyLock)
        {
            Array.Clear(_carrierSamples);
            Array.Clear(_totalSamples);
            _bucketSecond = -1;
        }
        Interlocked.Exchange(ref _levelUpdatedAtTicks, 0);
        Interlocked.Exchange(ref _carrierSinceTicks, 0);
        Interlocked.Exchange(ref _preamblesAtCarrierStart, 0);
    }

    public DateTime? LevelUpdatedAt => ToUtc(Interlocked.Read(ref _levelUpdatedAtTicks));

    public DateTime? CarrierSince => ToUtc(Interlocked.Read(ref _carrierSinceTicks));

    /// <summary>Preambles since the current burst began; 0 while the channel is clear.</summary>
    public long PreamblesSinceCarrier(long preambleTotal) =>
        Interlocked.Read(ref _carrierSinceTicks) == 0
            ? 0
            : Math.Max(0, preambleTotal - Interlocked.Read(ref _preamblesAtCarrierStart));

    private static DateTime? ToUtc(long ticks) => ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
}
