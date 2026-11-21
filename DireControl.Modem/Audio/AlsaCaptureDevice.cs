using System.Runtime.InteropServices;

namespace DireControl.Modem.Audio;

/// <summary>
/// Blocking ALSA PCM capture: mono S16_LE at the requested rate, converted to
/// float −1..1.  Designed to be driven by a dedicated thread — <see cref="Read"/>
/// blocks until samples are available.  ALSA's internal buffer is kept deep
/// (500 ms) so scheduling hiccups and GC pauses never drop audio.
/// </summary>
public sealed class AlsaCaptureDevice : IDisposable
{
    private const uint LatencyUs = 500_000;

    private readonly IntPtr _pcm;
    private readonly short[] _pcmBuffer;
    private readonly GCHandle _pcmBufferHandle;
    private bool _disposed;
    private long _overruns;

    public string DeviceName { get; }
    public int SampleRate { get; }

    /// <summary>
    /// Capture overruns recovered so far.  Each one is audio the demodulator
    /// never saw — previously recovered silently, so lost packets were invisible.
    /// </summary>
    public long Overruns => Interlocked.Read(ref _overruns);

    /// <summary>True when the explicit period request took effect.</summary>
    public bool ExplicitPeriod { get; }

    /// <param name="deviceName">ALSA PCM name, e.g. "default", "hw:1,0", "plughw:CARD=Device".</param>
    /// <param name="sampleRate">Capture rate; ALSA soft-resamples if the hardware differs.</param>
    /// <param name="periodFrames">Frames returned per <see cref="Read"/> (default ~21 ms at 48 kHz).</param>
    /// <param name="periodUs">
    /// Hardware period to request explicitly, or null for ALSA's default
    /// (buffer/4 — 125 ms with the 500 ms buffer, which makes carrier detect
    /// and CSMA react 125–250 ms late).  If the device refuses, capture falls
    /// back to the default configuration.
    /// </param>
    public AlsaCaptureDevice(string deviceName, int sampleRate = 48000, int periodFrames = 1024, uint? periodUs = null)
    {
        DeviceName = deviceName;
        SampleRate = sampleRate;
        _pcmBuffer = new short[periodFrames];
        _pcmBufferHandle = GCHandle.Alloc(_pcmBuffer, GCHandleType.Pinned);

        var err = AlsaNative.snd_pcm_open(out _pcm, deviceName, AlsaNative.SndPcmStreamCapture, mode: 0);
        if (err < 0)
        {
            _pcmBufferHandle.Free();
            throw new AlsaException($"snd_pcm_open(\"{deviceName}\")", err);
        }

        if (periodUs is { } period && TrySetExplicitPeriod(period, (uint)sampleRate))
        {
            ExplicitPeriod = true;
            return;
        }

        err = AlsaNative.snd_pcm_set_params(
            _pcm,
            AlsaNative.SndPcmFormatS16Le,
            AlsaNative.SndPcmAccessRwInterleaved,
            channels: 1,
            rate: (uint)sampleRate,
            softResample: 1,
            LatencyUs);
        if (err < 0)
        {
            AlsaNative.snd_pcm_close(_pcm);
            _pcmBufferHandle.Free();
            throw new AlsaException($"snd_pcm_set_params(\"{deviceName}\", {sampleRate} Hz)", err);
        }
    }

    /// <summary>
    /// Configures mono S16 at <paramref name="rate"/> with a short hardware
    /// period inside the usual 500 ms buffer.  Returns false — leaving nothing
    /// committed — if the device cannot honour any of it exactly.
    /// </summary>
    private bool TrySetExplicitPeriod(uint periodUs, uint rate)
    {
        if (AlsaNative.snd_pcm_hw_params_malloc(out var hw) < 0)
            return false;
        try
        {
            var dir = 0;
            var actualRate = rate;
            var bufferUs = LatencyUs;
            var period = periodUs;
            return AlsaNative.snd_pcm_hw_params_any(_pcm, hw) >= 0
                && AlsaNative.snd_pcm_hw_params_set_access(_pcm, hw, AlsaNative.SndPcmAccessRwInterleaved) >= 0
                && AlsaNative.snd_pcm_hw_params_set_format(_pcm, hw, AlsaNative.SndPcmFormatS16Le) >= 0
                && AlsaNative.snd_pcm_hw_params_set_channels(_pcm, hw, 1) >= 0
                && AlsaNative.snd_pcm_hw_params_set_rate_resample(_pcm, hw, 1) >= 0
                && AlsaNative.snd_pcm_hw_params_set_rate_near(_pcm, hw, ref actualRate, ref dir) >= 0
                && actualRate == rate
                && AlsaNative.snd_pcm_hw_params_set_buffer_time_near(_pcm, hw, ref bufferUs, ref dir) >= 0
                && AlsaNative.snd_pcm_hw_params_set_period_time_near(_pcm, hw, ref period, ref dir) >= 0
                && AlsaNative.snd_pcm_hw_params(_pcm, hw) >= 0;
        }
        finally
        {
            AlsaNative.snd_pcm_hw_params_free(hw);
        }
    }

    /// <summary>
    /// Blocks until up to <c>periodFrames</c> samples are captured, writes them
    /// into <paramref name="destination"/> as floats, and returns the count.
    /// Transparently recovers from overruns.  Throws <see cref="AlsaException"/>
    /// on unrecoverable errors (e.g. device unplugged).
    /// </summary>
    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var maxFrames = Math.Min(destination.Length, _pcmBuffer.Length);

        while (true)
        {
            var read = AlsaNative.snd_pcm_readi(
                _pcm, _pcmBufferHandle.AddrOfPinnedObject(), (ulong)maxFrames);

            if (read >= 0)
            {
                for (var i = 0; i < (int)read; i++)
                    destination[i] = _pcmBuffer[i] / 32768f;
                return (int)read;
            }

            Interlocked.Increment(ref _overruns);
            var recovered = AlsaNative.snd_pcm_recover(_pcm, (int)read, silent: 1);
            if (recovered < 0)
                throw new AlsaException($"snd_pcm_readi(\"{DeviceName}\")", (int)read);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        AlsaNative.snd_pcm_close(_pcm);
        _pcmBufferHandle.Free();
    }
}
