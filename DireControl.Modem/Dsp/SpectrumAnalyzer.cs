namespace DireControl.Modem.Dsp;

/// <summary>
/// Rolling audio spectrum for the waterfall display: keeps the most recent
/// samples in a ring, and on demand computes a Hann-windowed radix-2 FFT,
/// returning the 0–4 kHz band as dB magnitudes quantised to bytes
/// (0 = −80 dBFS or less, 255 = 0 dBFS).
/// </summary>
public sealed class SpectrumAnalyzer(int sampleRate)
{
    /// <summary>FFT length — 2048 points ≈ 23.4 Hz/bin at 48 kHz.</summary>
    public const int FftSize = 2048;

    /// <summary>Displayed band upper edge.</summary>
    public const float MaxFrequencyHz = 4000f;

    private const float MinDb = -80f;

    private readonly float[] _ring = new float[FftSize];
    private readonly Lock _lock = new();
    private int _position;
    private long _totalWritten;

    private static readonly float[] HannWindow = BuildHannWindow();

    /// <summary>Number of returned bins (0 Hz … 4 kHz inclusive).</summary>
    public int BinCount => (int)(MaxFrequencyHz * FftSize / sampleRate) + 1;

    /// <summary>Hz covered by each returned bin.</summary>
    public double BinWidthHz => (double)sampleRate / FftSize;

    /// <summary>Appends captured samples to the ring (called from the audio thread).</summary>
    public void AddSamples(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            foreach (var s in samples)
            {
                _ring[_position] = s;
                _position = (_position + 1) % FftSize;
            }
            _totalWritten += samples.Length;
        }
    }

    /// <summary>
    /// Computes the current spectrum, or returns <see langword="null"/> until
    /// a full FFT window of audio has been captured.
    /// </summary>
    public byte[]? ComputeSpectrum()
    {
        var re = new float[FftSize];
        lock (_lock)
        {
            if (_totalWritten < FftSize)
                return null;

            // Unroll the ring so re[0] is the oldest sample.
            var idx = _position;
            for (var i = 0; i < FftSize; i++)
            {
                re[i] = _ring[idx] * HannWindow[i];
                idx = (idx + 1) % FftSize;
            }
        }

        var im = new float[FftSize];
        Fft(re, im);

        var bins = new byte[BinCount];
        // Hann window coherent gain is 0.5; scale so a full-scale sine reads ~0 dBFS.
        var scale = 4.0f / FftSize;
        for (var i = 0; i < bins.Length; i++)
        {
            var magnitude = MathF.Sqrt(re[i] * re[i] + im[i] * im[i]) * scale;
            var db = 20f * MathF.Log10(magnitude + 1e-9f);
            var normalized = (db - MinDb) / -MinDb; // −80 dB → 0, 0 dB → 1
            bins[i] = (byte)Math.Clamp((int)(normalized * 255f), 0, 255);
        }

        return bins;
    }

    /// <summary>In-place iterative radix-2 Cooley–Tukey FFT.</summary>
    private static void Fft(float[] re, float[] im)
    {
        var n = re.Length;

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j |= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * MathF.PI / len;
            var wRe = MathF.Cos(angle);
            var wIm = MathF.Sin(angle);

            for (var i = 0; i < n; i += len)
            {
                float curRe = 1, curIm = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var evenIdx = i + k;
                    var oddIdx = i + k + len / 2;

                    var oddRe = re[oddIdx] * curRe - im[oddIdx] * curIm;
                    var oddIm = re[oddIdx] * curIm + im[oddIdx] * curRe;

                    re[oddIdx] = re[evenIdx] - oddRe;
                    im[oddIdx] = im[evenIdx] - oddIm;
                    re[evenIdx] += oddRe;
                    im[evenIdx] += oddIm;

                    (curRe, curIm) = (curRe * wRe - curIm * wIm, curRe * wIm + curIm * wRe);
                }
            }
        }
    }

    private static float[] BuildHannWindow()
    {
        var window = new float[FftSize];
        for (var i = 0; i < FftSize; i++)
            window[i] = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (FftSize - 1)));
        return window;
    }
}
