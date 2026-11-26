using System.Threading.Channels;
using DireControl.Data.Models;

namespace DireControl.Api.Services;

/// <summary>One frame waiting for the RF ingest pipeline.</summary>
public readonly record struct RfIngestItem(
    byte[] Frame,
    int Channel,
    SignalData? Signal,
    bool IsOwnTransmission = false);

/// <summary>
/// Decouples the ALSA capture thread from the RF ingest pipeline.  Ingest does
/// database work (SQLite's async calls complete synchronously, and block for
/// as long as another writer holds the lock) plus KISS/AGWPE fan-out; run
/// inline in the demodulator callback, a 1.5 s lock overran the 500 ms capture
/// buffer and lost the digipeated copies that arrive right after a decode.
/// <see cref="Post"/> never blocks: frames queue in a bounded channel that
/// drops the oldest on overflow (counted in <see cref="Dropped"/>), and a
/// single consumer ingests them in arrival order.
/// </summary>
public sealed class RfIngestDispatcher
{
    public const int DefaultCapacity = 256;

    private readonly Channel<RfIngestItem> _channel;
    private long _dropped;

    public RfIngestDispatcher(int capacity = DefaultCapacity)
    {
        _channel = Channel.CreateBounded<RfIngestItem>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    /// <summary>Frames discarded because the ingest pipeline fell too far behind.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Queues a frame for ingest.  Never blocks the caller.</summary>
    public void Post(RfIngestItem item) => _channel.Writer.TryWrite(item);

    /// <summary>
    /// Consumes queued frames until <paramref name="ct"/> is cancelled.  A
    /// failing ingest is logged and skipped — it must never stop the loop.
    /// </summary>
    public async Task RunAsync(
        Func<RfIngestItem, CancellationToken, Task> ingest, ILogger logger, CancellationToken ct)
    {
        var reportedDropped = 0L;
        await foreach (var item in _channel.Reader.ReadAllAsync(ct))
        {
            var dropped = Dropped;
            if (dropped != reportedDropped)
            {
                logger.LogWarning(
                    "RF ingest fell behind; {Count} frame(s) dropped so far.", dropped);
                reportedDropped = dropped;
            }

            try
            {
                await ingest(item, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error processing modem frame.");
            }
        }
    }
}
