using DireControl.Modem.Dsp;

namespace DireControl.Modem.Audio;

/// <summary>
/// Converts the modem's 48 kHz mono float audio into a low-rate 16-bit PCM
/// stream suitable for shipping to a browser for listening.  Audio is
/// anti-alias lowpassed, decimated to <see cref="OutputSampleRate"/>, clamped,
/// and packed into fixed-size little-endian frames.
///
/// The output is deliberately a monitoring stream, not a second demodulation
/// path: the rate is chosen so the full voice band and both AFSK tones
/// (1200/2200 Hz) survive while staying cheap enough to stream over a WAN
/// link.  One instance is single-threaded and carries filter history between
/// calls, so each audio source needs its own.
/// </summary>
public sealed class AudioMonitorResampler
{
    /// <summary>Output rate in Hz — Nyquist 4 kHz, above the 2200 Hz space tone.</summary>
    public const int OutputSampleRate = 8000;

    /// <summary>Samples per emitted frame — 64 ms, a reasonable network chunk.</summary>
    public const int FrameSamples = 512;

    /// <summary>Bytes per emitted frame (16-bit mono).</summary>
    public const int FrameBytes = FrameSamples * 2;

    private const float AntiAliasCutoffHz = 3400f;
    private const int AntiAliasTaps = 63;

    private readonly FirFilter _antiAlias;
    private readonly int _decimation;
    private readonly byte[] _frame = new byte[FrameBytes];
    private int _frameBytesFilled;
    private int _phase;

    public AudioMonitorResampler(int inputSampleRate)
    {
        if (inputSampleRate <= 0 || inputSampleRate % OutputSampleRate != 0)
            throw new ArgumentOutOfRangeException(
                nameof(inputSampleRate),
                inputSampleRate,
                $"Input rate must be a positive multiple of {OutputSampleRate} Hz.");

        _decimation = inputSampleRate / OutputSampleRate;
        _antiAlias = new FirFilter(
            FirFilter.LowpassTaps(inputSampleRate, AntiAliasCutoffHz, AntiAliasTaps));
    }

    /// <summary>
    /// Pushes input samples through the resampler, invoking
    /// <paramref name="onFrame"/> once per completed frame.  The callback
    /// receives a fresh array it may keep; partial frames are retained for the
    /// next call.
    /// </summary>
    public void Process(ReadOnlySpan<float> samples, Action<byte[]> onFrame)
    {
        foreach (var sample in samples)
        {
            // Every input sample must run through the filter to keep its
            // history continuous; only every Nth output is kept.
            var filtered = _antiAlias.Process(sample);

            if (++_phase < _decimation)
                continue;
            _phase = 0;

            var clamped = Math.Clamp(filtered, -1f, 1f);
            var pcm = (short)(clamped * short.MaxValue);
            _frame[_frameBytesFilled++] = (byte)(pcm & 0xFF);
            _frame[_frameBytesFilled++] = (byte)((pcm >> 8) & 0xFF);

            if (_frameBytesFilled < FrameBytes)
                continue;

            _frameBytesFilled = 0;
            onFrame(_frame.ToArray());
        }
    }

    /// <summary>
    /// Drops filter history and any partial frame — call when the audio source
    /// restarts so stale samples cannot leak into the next stream.
    /// </summary>
    public void Reset()
    {
        _antiAlias.Reset();
        _frameBytesFilled = 0;
        _phase = 0;
    }
}
