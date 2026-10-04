using System.Threading.Channels;
using DireControl.Modem.Audio;

namespace DireControl.Api.Services;

/// <summary>
/// Describes the wire format of the audio monitor stream so clients do not
/// have to hardcode it.
/// </summary>
public sealed record AudioStreamFormat(int SampleRate, int FrameSamples, string Encoding)
{
    public static AudioStreamFormat Current { get; } = new(
        AudioMonitorResampler.OutputSampleRate,
        AudioMonitorResampler.FrameSamples,
        "pcm_s16le");
}

/// <summary>
/// Fans modem audio out from the capture/transmit threads to any number of
/// listening clients.  The publish path is lock-free and allocation-free when
/// nobody is listening, because it runs on the realtime DSP thread and must
/// never stall it: callers check <see cref="HasListeners"/> before paying for
/// resampling at all, and a slow client drops frames rather than applying
/// backpressure to the modem.
/// </summary>
public sealed class RadioAudioBroker
{
    /// <summary>
    /// Frames buffered per client before the oldest are dropped.  Sized to
    /// absorb a whole transmit burst (which arrives faster than real time,
    /// since the TX buffer is rendered in one go) without unbounded growth.
    /// </summary>
    private const int ClientQueueFrames = 64;

    /// <summary>
    /// How long a squelch-gated stream keeps flowing after carrier drops, so
    /// the tail of a transmission is not clipped off.
    /// </summary>
    private static readonly TimeSpan SquelchHang = TimeSpan.FromMilliseconds(750);

    private sealed class Listener(string radioId, bool squelchGated, bool includeTx)
    {
        public string RadioId { get; } = radioId;
        public bool SquelchGated { get; } = squelchGated;
        public bool IncludeTx { get; } = includeTx;

        public Channel<byte[]> Frames { get; } = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(ClientQueueFrames)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });

        private long _openUntilTicks;

        /// <summary>Opens the squelch gate for the hang period.</summary>
        public void MarkCarrier() =>
            Volatile.Write(ref _openUntilTicks, DateTime.UtcNow.Add(SquelchHang).Ticks);

        public bool GateOpen =>
            !SquelchGated || DateTime.UtcNow.Ticks <= Volatile.Read(ref _openUntilTicks);
    }

    // Copy-on-write so the publish path can enumerate without taking a lock.
    private volatile Listener[] _listeners = [];
    private readonly object _gate = new();

    /// <summary>
    /// True when at least one client is listening to this radio.  Checked on
    /// the DSP thread before resampling, so idle radios cost one array read.
    /// </summary>
    public bool HasListeners(string radioId)
    {
        foreach (var listener in _listeners)
            if (listener.RadioId == radioId)
                return true;
        return false;
    }

    /// <summary>True when a client wants this radio's transmit audio mixed in.</summary>
    public bool HasTxListeners(string radioId)
    {
        foreach (var listener in _listeners)
            if (listener.RadioId == radioId && listener.IncludeTx)
                return true;
        return false;
    }

    /// <summary>
    /// Publishes a frame of received audio.  <paramref name="carrierDetected"/>
    /// drives the squelch gate for clients that asked for one.
    /// </summary>
    public void PublishReceive(string radioId, byte[] frame, bool carrierDetected)
    {
        foreach (var listener in _listeners)
        {
            if (listener.RadioId != radioId)
                continue;

            if (carrierDetected)
                listener.MarkCarrier();

            if (listener.GateOpen)
                listener.Frames.Writer.TryWrite(frame);
        }
    }

    /// <summary>
    /// Publishes a frame of transmit audio.  Only reaches clients that opted
    /// into TX monitoring, and always bypasses the squelch gate — you are
    /// listening to your own keyup, there is no carrier to detect.
    /// </summary>
    public void PublishTransmit(string radioId, byte[] frame)
    {
        foreach (var listener in _listeners)
            if (listener.RadioId == radioId && listener.IncludeTx)
                listener.Frames.Writer.TryWrite(frame);
    }

    /// <summary>
    /// Streams audio frames for one radio until the caller stops enumerating
    /// or <paramref name="ct"/> fires.
    /// </summary>
    public async IAsyncEnumerable<byte[]> ListenAsync(
        string radioId,
        bool squelchGated,
        bool includeTx,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var listener = new Listener(radioId, squelchGated, includeTx);
        Add(listener);
        try
        {
            await foreach (var frame in listener.Frames.Reader.ReadAllAsync(ct))
                yield return frame;
        }
        finally
        {
            Remove(listener);
        }
    }

    private void Add(Listener listener)
    {
        lock (_gate)
            _listeners = [.. _listeners, listener];
    }

    private void Remove(Listener listener)
    {
        lock (_gate)
            _listeners = [.. _listeners.Where(l => l != listener)];
        listener.Frames.Writer.TryComplete();
    }
}
