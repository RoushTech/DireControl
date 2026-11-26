using DireControl.Modem;
using DireControl.Modem.Ax25;
using DireControl.Modem.Dsp;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Optional TX pre-emphasis: the space tone is sent louder than mark so a
/// de-emphasised receiver hears them balanced.  0 dB must be unchanged.
/// </summary>
[TestFixture]
public sealed class TxSpaceBoostTests
{
    private const int SampleRate = 48000;

    private static double Goertzel(ReadOnlySpan<float> samples, double freq)
    {
        var w = 2 * Math.PI * freq / SampleRate;
        var coeff = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        foreach (var x in samples)
        {
            var s0 = x + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return Math.Sqrt(s1 * s1 + s2 * s2 - coeff * s1 * s2);
    }

    [TestCase(0.0)]
    [TestCase(2.0)]
    [TestCase(4.5)]
    [TestCase(6.0)]
    public void SpaceBoost_SetsToneRatioAndKeepsPeakAndDecodes(double boostDb)
    {
        var modulator = new AfskModulator(SampleRate) { SpaceBoostDb = boostDb };

        var frame = Ax25Encoder.EncodeUiFrame("N4WR-1", "!3518.00N/08508.00W-boost test " + new string('U', 60), "WIDE1-1");
        var audio = modulator.GenerateTransmission([frame], leadFlags: 30, tailFlags: 2, amplitude: 0.8f);

        // Per-symbol amplitude: sample the envelope at each bit centre and
        // classify by which tone dominates.
        var samplesPerBit = SampleRate / 1200.0;
        var bits = AfskModulator.BuildHdlcBitStream(frame, 30, 2);
        var level = true;
        double markPeak = 0, spacePeak = 0;
        for (var b = 0; b < bits.Count; b++)
        {
            if (!bits[b]) level = !level;
            var start = (int)Math.Round(b * samplesPerBit);
            var end = Math.Min(audio.Length, (int)Math.Round((b + 1) * samplesPerBit));
            var peak = 0f;
            for (var i = start; i < end; i++) peak = MathF.Max(peak, MathF.Abs(audio[i]));
            if (end >= audio.Length - SampleRate / 100) break; // skip the ramp-out
            if (level) markPeak = Math.Max(markPeak, peak); else spacePeak = Math.Max(spacePeak, peak);
        }

        var measuredDb = 20 * Math.Log10(spacePeak / markPeak);
        Assert.That(measuredDb, Is.EqualTo(boostDb).Within(0.5), "space/mark level ratio");
        Assert.That(audio.Max(MathF.Abs), Is.LessThanOrEqualTo(0.8f + 1e-4f), "peak never exceeds amplitude");

        var receiver = AfskReceiver.CreateStandard(SampleRate);
        var decoded = new List<byte[]>();
        receiver.FrameReceived += (f, _) => decoded.Add(f);
        receiver.ProcessSamples(audio);
        receiver.ProcessSamples(new float[SampleRate / 10]);
        Assert.That(decoded, Has.Count.EqualTo(1));
        Assert.That(decoded[0], Is.EqualTo(frame).AsCollection);
    }

    [Test]
    public void SpaceBoost_MeasuredWithGoertzelOnSteadyTones()
    {
        // A preamble of flags is mostly mark with regular space cells; compare
        // Goertzel energy at each tone between boosted and flat renders.
        var frame = Ax25Encoder.EncodeUiFrame("N4WR-1", "x", null);
        var flat = new AfskModulator(SampleRate).GenerateTransmission([frame], 60, 1, 0.8f);
        var boosted = new AfskModulator(SampleRate) { SpaceBoostDb = 5 }.GenerateTransmission([frame], 60, 1, 0.8f);

        var flatRatio = Goertzel(flat, 2200) / Goertzel(flat, 1200);
        var boostedRatio = Goertzel(boosted, 2200) / Goertzel(boosted, 1200);

        Assert.That(20 * Math.Log10(boostedRatio / flatRatio), Is.EqualTo(5.0).Within(0.5));
    }
}
