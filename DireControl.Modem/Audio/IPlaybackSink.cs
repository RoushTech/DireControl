namespace DireControl.Modem.Audio;

/// <summary>
/// Where transmit audio goes.  Opened (constructed) before PTT is keyed, so a
/// device that cannot be opened never puts dead carrier on the air.
/// </summary>
public interface IPlaybackSink : IDisposable
{
    /// <summary>Queues samples for playback; may block while the device buffer is full.</summary>
    void Write(ReadOnlySpan<float> samples);

    /// <summary>Blocks until every queued sample has played out.</summary>
    void Drain();
}
