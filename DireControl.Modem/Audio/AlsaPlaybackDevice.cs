using System.Runtime.InteropServices;

namespace DireControl.Modem.Audio;

/// <summary>
/// Blocking ALSA PCM playback: mono S16_LE at the requested rate.  Opened per
/// transmission and disposed after <see cref="Drain"/>, so the device is only
/// held while actually transmitting.
/// </summary>
public sealed class AlsaPlaybackDevice : IDisposable
{
    private const uint LatencyUs = 200_000;
    private const int PeriodFrames = 1024;

    private readonly IntPtr _pcm;
    private readonly short[] _pcmBuffer;
    private readonly GCHandle _pcmBufferHandle;
    private bool _disposed;

    public string DeviceName { get; }

    public AlsaPlaybackDevice(string deviceName, int sampleRate = 48000)
    {
        DeviceName = deviceName;
        _pcmBuffer = new short[PeriodFrames];
        _pcmBufferHandle = GCHandle.Alloc(_pcmBuffer, GCHandleType.Pinned);

        var err = AlsaNative.snd_pcm_open(out _pcm, deviceName, AlsaNative.SndPcmStreamPlayback, mode: 0);
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
    /// Writes the whole buffer to the device, converting float −1..1 to S16,
    /// recovering transparently from underruns.  Blocks until ALSA has
    /// accepted all samples (not until they have played — call <see cref="Drain"/>).
    /// </summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var offset = 0;
        while (offset < samples.Length)
        {
            var chunk = Math.Min(PeriodFrames, samples.Length - offset);
            for (var i = 0; i < chunk; i++)
                _pcmBuffer[i] = (short)(Math.Clamp(samples[offset + i], -1f, 1f) * 32767f);

            var written = AlsaNative.snd_pcm_writei(
                _pcm, _pcmBufferHandle.AddrOfPinnedObject(), (ulong)chunk);

            if (written < 0)
            {
                var recovered = AlsaNative.snd_pcm_recover(_pcm, (int)written, silent: 1);
                if (recovered < 0)
                    throw new AlsaException($"snd_pcm_writei(\"{DeviceName}\")", (int)written);
                continue;
            }

            offset += (int)written;
        }
    }

    /// <summary>Blocks until every queued sample has actually played out.</summary>
    public void Drain()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AlsaNative.snd_pcm_drain(_pcm);
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
