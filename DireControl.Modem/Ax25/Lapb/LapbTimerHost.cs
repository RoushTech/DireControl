namespace DireControl.Modem.Ax25.Lapb;

/// <summary>
/// Production timer adapter for a <see cref="LapbStateMachine"/>: subscribes
/// to its timer events and fires expiries back through a host-supplied
/// serializer so timer callbacks never race other machine inputs.  Tests
/// bypass this class entirely and drive
/// <see cref="LapbStateMachine.OnTimerExpired"/> directly.
/// </summary>
public sealed class LapbTimerHost : IDisposable
{
    private readonly Timer _t1;
    private readonly Timer _t2;
    private readonly Timer _t3;
    private volatile bool _disposed;

    /// <param name="machine">The machine whose timer events to service.</param>
    /// <param name="post">
    /// Serializer for machine input — typically the owning session's mailbox.
    /// The posted action calls <see cref="LapbStateMachine.OnTimerExpired"/>.
    /// </param>
    public LapbTimerHost(LapbStateMachine machine, Action<Action> post)
    {
        _t1 = new Timer(_ => Fire(LapbTimer.T1), null, Timeout.Infinite, Timeout.Infinite);
        _t2 = new Timer(_ => Fire(LapbTimer.T2), null, Timeout.Infinite, Timeout.Infinite);
        _t3 = new Timer(_ => Fire(LapbTimer.T3), null, Timeout.Infinite, Timeout.Infinite);

        machine.StartTimer += Start;
        machine.StopTimer += Stop;

        void Fire(LapbTimer timer)
        {
            if (!_disposed)
                post(() => machine.OnTimerExpired(timer));
        }
    }

    private void Start(LapbTimer timer, TimeSpan duration)
    {
        if (!_disposed)
            Get(timer).Change(duration, Timeout.InfiniteTimeSpan);
    }

    private void Stop(LapbTimer timer)
    {
        if (!_disposed)
            Get(timer).Change(Timeout.Infinite, Timeout.Infinite);
    }

    private Timer Get(LapbTimer timer) => timer switch
    {
        LapbTimer.T1 => _t1,
        LapbTimer.T2 => _t2,
        _ => _t3,
    };

    public void Dispose()
    {
        _disposed = true;
        _t1.Dispose();
        _t2.Dispose();
        _t3.Dispose();
    }
}
