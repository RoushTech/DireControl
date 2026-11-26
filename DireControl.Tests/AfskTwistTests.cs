using DireControl.Modem;
using DireControl.Modem.Ax25;
using DireControl.Modem.Dsp;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Mark/space amplitude imbalance ("twist") tolerance.  A station whose TNC
/// feeds a pre-emphasised mic input arrives with space ~5 dB hot; a
/// de-emphasised receiver path does the opposite.  The correlator arms must
/// isolate the tones well enough that the per-tone AGC can normalise either
/// imbalance away.
/// </summary>
[TestFixture]
public sealed class AfskTwistTests
{
    private const int SampleRate = 48000;
    private const float LouderToneAmplitude = 0.5f;

    private static byte[] MakeTestFrame(int seed) =>
        Ax25Encoder.EncodeUiFrame(
            $"N4WR-{seed % 15 + 1}",
            $"!3518.00N/08508.00W-Twist test frame {seed} with some payload text.",
            "WIDE1-1,WIDE2-1");

    /// <summary>
    /// Renders a phase-continuous AFSK burst with independent mark and space
    /// amplitudes.  <paramref name="spaceTwistDb"/> is space relative to mark;
    /// the louder tone is always <see cref="LouderToneAmplitude"/>.
    /// </summary>
    internal static float[] RenderWithTwist(byte[] frame, double spaceTwistDb, int leadFlags = 30)
    {
        var ratio = Math.Pow(10, spaceTwistDb / 20);
        var markAmp = ratio > 1 ? LouderToneAmplitude / ratio : LouderToneAmplitude;
        var spaceAmp = ratio > 1 ? LouderToneAmplitude : LouderToneAmplitude * ratio;

        var bits = AfskModulator.BuildHdlcBitStream(frame, leadFlags, 3);
        var samplesPerBit = SampleRate / 1200.0;
        var samples = new List<float>((int)(bits.Count * samplesPerBit) + 1);

        var level = true;
        var phase = 0.0;
        var boundary = 0.0;
        foreach (var bit in bits)
        {
            if (!bit)
                level = !level;
            var freq = level ? 1200.0 : 2200.0;
            var amp = level ? markAmp : spaceAmp;
            boundary += samplesPerBit;
            while (samples.Count < (int)Math.Round(boundary))
            {
                samples.Add((float)(Math.Sin(phase) * amp));
                phase += 2 * Math.PI * freq / SampleRate;
                if (phase > 2 * Math.PI)
                    phase -= 2 * Math.PI;
            }
        }
        return [.. samples];
    }

    private static int CountDecoded(double twistDb, double noiseSigma, int frames, int seed)
    {
        var rng = new Random(seed);
        var decodedCount = 0;
        for (var i = 0; i < frames; i++)
        {
            var frame = MakeTestFrame(i);
            var audio = RenderWithTwist(frame, twistDb);
            if (noiseSigma > 0)
                for (var s = 0; s < audio.Length; s++)
                    audio[s] += (float)(Gaussian(rng) * noiseSigma);

            var receiver = AfskReceiver.CreateStandard(SampleRate);
            var decoded = new List<byte[]>();
            receiver.FrameReceived += (f, _) => decoded.Add(f);
            for (var pos = 0; pos < audio.Length; pos += 1024)
                receiver.ProcessSamples(audio.AsSpan(pos, Math.Min(1024, audio.Length - pos)));
            receiver.ProcessSamples(new float[SampleRate / 10]);

            if (decoded.Count == 1 && decoded[0].AsSpan().SequenceEqual(frame))
                decodedCount++;
        }
        return decodedCount;
    }

    private static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    [TestCase(-9.0)]
    [TestCase(-6.0)]
    [TestCase(-3.0)]
    [TestCase(0.0)]
    [TestCase(2.0)]
    [TestCase(3.0)]
    [TestCase(5.3, Description = "Pre-emphasised mic-input sender")]
    [TestCase(6.0)]
    [TestCase(9.0)]
    public void Twist_NoNoise_DecodesEveryFrame(double spaceTwistDb)
    {
        var decoded = CountDecoded(spaceTwistDb, noiseSigma: 0, frames: 20, seed: 1);
        Assert.That(decoded, Is.EqualTo(20), $"decoded {decoded}/20 at space twist {spaceTwistDb:+0.0;-0.0} dB");
    }

    [TestCase(-9.0)]
    [TestCase(-6.0)]
    [TestCase(-3.0)]
    [TestCase(0.0)]
    [TestCase(3.0)]
    [TestCase(6.0)]
    [TestCase(9.0)]
    public void Twist_WithNoise_DecodesAtLeast90Percent(double spaceTwistDb)
    {
        var decoded = CountDecoded(spaceTwistDb, noiseSigma: 0.12, frames: 20, seed: 4321);
        Assert.That(decoded, Is.GreaterThanOrEqualTo(18),
            $"decoded {decoded}/20 at space twist {spaceTwistDb:+0.0;-0.0} dB, noise σ 0.12");
    }

    /// <summary>
    /// Each correlator arm must reject the other tone by at least 20 dB —
    /// otherwise the per-tone AGC can normalise the leakage up to look like
    /// the arm's own tone, and the slicer never changes sign under twist.
    /// </summary>
    [Test]
    public void Correlator_IsolatesMarkAndSpaceBy20Db()
    {
        var profile = DemodProfile.Standard(SampleRate);

        static float SteadyEnvelope(ToneCorrelator correlator, float toneHz)
        {
            var peak = 0f;
            for (var n = 0; n < SampleRate / 10; n++)
            {
                var env = correlator.Process((float)Math.Sin(2 * Math.PI * toneHz * n / SampleRate));
                if (n > SampleRate / 50)
                    peak = MathF.Max(peak, env);
            }
            return peak;
        }

        var markOnMark = SteadyEnvelope(new ToneCorrelator(SampleRate, 1200f, profile.CorrelatorTaps), 1200f);
        var markOnSpace = SteadyEnvelope(new ToneCorrelator(SampleRate, 1200f, profile.CorrelatorTaps), 2200f);
        var spaceOnSpace = SteadyEnvelope(new ToneCorrelator(SampleRate, 2200f, profile.CorrelatorTaps), 2200f);
        var spaceOnMark = SteadyEnvelope(new ToneCorrelator(SampleRate, 2200f, profile.CorrelatorTaps), 1200f);

        var markIsolationDb = 20 * Math.Log10(markOnMark / Math.Max(markOnSpace, 1e-9f));
        var spaceIsolationDb = 20 * Math.Log10(spaceOnSpace / Math.Max(spaceOnMark, 1e-9f));

        Assert.Multiple(() =>
        {
            Assert.That(markIsolationDb, Is.GreaterThanOrEqualTo(20), "mark arm vs space tone");
            Assert.That(spaceIsolationDb, Is.GreaterThanOrEqualTo(20), "space arm vs mark tone");
        });
    }
}
