namespace DireControl.Tests.Fakes;

/// <summary>
/// A virtual clock for code with a single waiter: every timer it creates
/// jumps the clock forward by its due time and fires straight away.  Waits
/// cost no wall-clock time, and <see cref="Delays"/> records each one so a
/// test can assert on how the code under test paced itself.
/// </summary>
internal sealed class AutoAdvanceTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long _ticks;
    private readonly List<TimeSpan> _delays = [];

    public IReadOnlyList<TimeSpan> Delays
    {
        get { lock (_delays) return [.. _delays]; }
    }

    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    /// <summary>Moves the clock without a timer (e.g. from inside a callback under test).</summary>
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => Epoch + Elapsed;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (_delays) _delays.Add(dueTime);
            Advance(dueTime);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
        return new InertTimer();
    }

    private sealed class InertTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
