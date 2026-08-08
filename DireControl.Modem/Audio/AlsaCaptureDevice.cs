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

    public string DeviceName { get; }
    public int SampleRate { get; }

    /// <param name="deviceName">ALSA PCM name, e.g. "default", "hw:1,0", "plughw:CARD=Device".</param>
    /// <param name="sampleRate">Capture rate; ALSA soft-resamples if the hardware differs.</param>
    /// <param name="periodFrames">Frames returned per <see cref="Read"/> (default ~21 ms at 48 kHz).</param>
    public AlsaCaptureDevice(string deviceName, int sampleRate = 48000, int periodFrames = 1024)
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
