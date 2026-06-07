using System.Threading.Channels;
using DireControl.Api.Controllers.Models;

namespace DireControl.Api.Logging;

/// <summary>
/// In-memory hub between the <see cref="SignalRLoggerProvider"/> (producer) and
/// the <see cref="LogBroadcastService"/> (consumer that pushes to SignalR).
///
/// Holds two things:
///   • a bounded channel for live delivery (drops oldest under back-pressure so
///     logging never blocks an application thread), and
///   • a fixed-size ring buffer of the most recent entries so a freshly-connected
///     client gets immediate backlog instead of a blank screen.
///
/// This type deliberately performs no logging of its own — doing so would feed
/// the log pipeline back into itself.
/// </summary>
public sealed class LogStreamBroadcaster
{
    private const int RingCapacity = 500;
    private const int ChannelCapacity = 4000;

    private readonly Lock _ringLock = new();
    private readonly Queue<LogEntryDto> _ring = new(RingCapacity);
    private long _sequence;

    private readonly Channel<LogEntryDto> _channel =
        Channel.CreateBounded<LogEntryDto>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelReader<LogEntryDto> Reader => _channel.Reader;

    /// <summary>
    /// Records a log entry into the ring buffer and offers it for live broadcast.
    /// Never blocks: the channel drops its oldest entry when full.
    /// </summary>
    public void Publish(DateTime timestampUtc, string level, string category, string message, string? exception)
    {
        var entry = new LogEntryDto
        {
            Sequence = Interlocked.Increment(ref _sequence),
            Timestamp = timestampUtc,
            Level = level,
            Category = category,
            Message = message,
            Exception = exception,
        };

        lock (_ringLock)
        {
            if (_ring.Count >= RingCapacity)
                _ring.Dequeue();
            _ring.Enqueue(entry);
        }

        _channel.Writer.TryWrite(entry);
    }

    /// <summary>Most recent entries, oldest first, for seeding a new client.</summary>
    public IReadOnlyList<LogEntryDto> Snapshot()
    {
        lock (_ringLock)
            return _ring.ToArray();
    }
}
