using System.Threading.Channels;

namespace DireControl.Api.Services;

/// <summary>
/// Bounded queue of TNC2 lines waiting to be sent to APRS-IS (RF→IS gating).
/// <see cref="RfFrameIngestService"/> enqueues; <see cref="AprsIsService"/>
/// drains it while connected and verified.  When disconnected the queue fills
/// and the oldest lines drop — gating is best-effort, never a backlog.
/// </summary>
public sealed class AprsIsTxQueue
{
    private const int Capacity = 100;

    private readonly Channel<string> _channel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private long _sentCount;

    public ChannelReader<string> Reader => _channel.Reader;

    /// <summary>Lines successfully handed to the APRS-IS client.</summary>
    public long SentCount => Interlocked.Read(ref _sentCount);

    public void Enqueue(string tnc2Line) => _channel.Writer.TryWrite(tnc2Line);

    public void RecordSent() => Interlocked.Increment(ref _sentCount);
}
