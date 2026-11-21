using System.Numerics;

namespace DireControl.Modem.Dsp;

/// <summary>
/// Direct-form FIR filter.  Coefficient factories generate windowed-sinc
/// (lowpass/bandpass) and rectangular-quadrature (correlator) tap sets.
/// </summary>
/// <remarks>
/// The history is written twice, <c>Length</c> apart, so the most recent
/// <c>Length</c> samples are always one contiguous span and the dot product
/// vectorises — about four times faster than walking a circular buffer, and
/// these filters are essentially the whole cost of the receiver.
/// </remarks>
public sealed class FirFilter
{
    // Taps reversed, so index 0 pairs with the oldest sample in the window.
    private readonly float[] _reversedTaps;
    private readonly float[] _history;
    private readonly int _length;
    private int _position;

    public FirFilter(float[] taps)
    {
        _length = taps.Length;
        _reversedTaps = new float[_length];
        for (var i = 0; i < _length; i++)
            _reversedTaps[i] = taps[_length - 1 - i];
        _history = new float[_length * 2];
    }

    public int Length => _length;

    /// <summary>
    /// Pushes one sample through the filter and returns the filtered output.
    /// </summary>
    public float Process(float sample)
    {
        _history[_position] = sample;
        _history[_position + _length] = sample;
        if (++_position == _length)
            _position = 0;

        // Oldest → newest, contiguous.
        var window = _history.AsSpan(_position, _length);
        var taps = _reversedTaps.AsSpan();

        var acc = Vector<float>.Zero;
        var i = 0;
        for (; i <= _length - Vector<float>.Count; i += Vector<float>.Count)
            acc += new Vector<float>(window[i..]) * new Vector<float>(taps[i..]);

        var sum = Vector.Sum(acc);
        for (; i < _length; i++)
            sum += window[i] * taps[i];
        return sum;
    }

    public void Reset()
    {
        Array.Clear(_history);
        _position = 0;
    }

    // ── Coefficient factories ─────────────────────────────────────────────────

    /// <summary>
    /// Windowed-sinc lowpass filter taps (Blackman window), unity DC gain.
    /// </summary>
    public static float[] LowpassTaps(int sampleRate, float cutoffHz, int tapCount)
    {
        var taps = new float[tapCount];
        var fc = cutoffHz / sampleRate;
        var center = (tapCount - 1) / 2.0;

        var sum = 0.0;
        for (var i = 0; i < tapCount; i++)
        {
            var x = i - center;
            var sinc = x == 0 ? 2 * Math.PI * fc : Math.Sin(2 * Math.PI * fc * x) / x;
            var w = Blackman(i, tapCount);
            taps[i] = (float)(sinc * w);
            sum += taps[i];
        }

        // Normalise to unity gain at DC.
        for (var i = 0; i < tapCount; i++)
            taps[i] = (float)(taps[i] / sum);

        return taps;
    }

    /// <summary>
    /// Kaiser-windowed-sinc bandpass taps (linear phase, unity passband gain).
    /// <paramref name="beta"/> trades stopband depth against transition width;
    /// 5 gives roughly 50 dB of rejection.
    /// </summary>
    public static float[] KaiserBandpassTaps(
        int sampleRate, float lowHz, float highHz, int tapCount, double beta = 5.0)
    {
        var taps = new float[tapCount];
        var center = (tapCount - 1) / 2.0;
        var i0Beta = BesselI0(beta);

        for (var i = 0; i < tapCount; i++)
        {
            var x = i - center;
            var ideal = x == 0
                ? 2.0 * (highHz - lowHz) / sampleRate
                : (Math.Sin(2 * Math.PI * highHz / sampleRate * x)
                    - Math.Sin(2 * Math.PI * lowHz / sampleRate * x)) / (Math.PI * x);
            var r = 2.0 * i / (tapCount - 1) - 1;
            taps[i] = (float)(ideal * BesselI0(beta * Math.Sqrt(1 - r * r)) / i0Beta);
        }

        return taps;
    }

    /// <summary>Zeroth-order modified Bessel function of the first kind (series).</summary>
    private static double BesselI0(double x)
    {
        double sum = 1, term = 1;
        for (var k = 1; k < 30; k++)
        {
            term *= (x / (2 * k)) * (x / (2 * k));
            sum += term;
        }
        return sum;
    }

    /// <summary>
    /// Rectangular-window quadrature (sine or cosine) taps at
    /// <paramref name="toneHz"/> — one arm of a tone correlator / matched filter.
    /// <para>
    /// The window is deliberately rectangular: a rectangular window of length T
    /// has spectral nulls every 1/T, so at exactly 1.2 symbols (1 ms) the mark
    /// arm nulls the 2200 Hz space tone and vice versa.  A Blackman taper moved
    /// the first null out to ~3 kHz and left only ~4.7 dB of isolation, after
    /// which the per-tone AGC normalised the leakage up and any twist past
    /// ~2 dB stopped every decode.
    /// </para>
    /// </summary>
    public static float[] QuadratureTaps(int sampleRate, float toneHz, int tapCount, bool cosine)
    {
        var taps = new float[tapCount];
        var center = (tapCount - 1) / 2.0;

        for (var i = 0; i < tapCount; i++)
        {
            var angle = 2 * Math.PI * toneHz * (i - center) / sampleRate;
            taps[i] = (float)((cosine ? Math.Cos(angle) : Math.Sin(angle)) / tapCount);
        }

        return taps;
    }

    private static double Blackman(int i, int tapCount)
    {
        var t = (double)i / (tapCount - 1);
        return 0.42 - 0.5 * Math.Cos(2 * Math.PI * t) + 0.08 * Math.Cos(4 * Math.PI * t);
    }
}
