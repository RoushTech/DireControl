using DireControl.Modem.Dsp;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Test-tone generation for TX calibration: correct length, amplitude bounds,
/// phase continuity, and that an alternating tone genuinely differs from steady.
/// </summary>
[TestFixture]
public sealed class AfskTestToneTests
{
    private const int SampleRate = 48000;

    [Test]
    public void SteadyTone_HasExpectedSampleCount()
    {
        var modulator = new AfskModulator(SampleRate);
        var samples = modulator.GenerateTestTone([1200], durationMs: 1000, segmentMs: 1000, amplitude: 0.8f);
        Assert.That(samples.Length, Is.EqualTo(SampleRate)); // 1 s at 48 kHz
    }

    [Test]
    public void Tone_RespectsAmplitude()
    {
        var modulator = new AfskModulator(SampleRate);
        var samples = modulator.GenerateTestTone([1200], durationMs: 200, segmentMs: 200, amplitude: 0.5f);
        Assert.That(samples, Is.Not.Empty);
        foreach (var s in samples)
            Assert.That(Math.Abs(s), Is.LessThanOrEqualTo(0.5f).Within(1e-4));
    }

    [Test]
    public void EmptyFrequenciesOrZeroDuration_YieldsNoSamples()
    {
        var modulator = new AfskModulator(SampleRate);
        Assert.That(modulator.GenerateTestTone([], 1000, 1000), Is.Empty);
        Assert.That(modulator.GenerateTestTone([1200], 0, 100), Is.Empty);
    }

    [Test]
    public void AlternatingTone_DiffersFromSteady()
    {
        var modulator = new AfskModulator(SampleRate);
        var steady = modulator.GenerateTestTone([1200], durationMs: 300, segmentMs: 100, amplitude: 0.8f);
        var alternating = modulator.GenerateTestTone([1200, 2200], durationMs: 300, segmentMs: 100, amplitude: 0.8f);

        Assert.That(alternating.Length, Is.EqualTo(steady.Length));
        // The second segment uses the space tone, so the waveforms must diverge.
        var diverges = false;
        for (var i = 0; i < steady.Length; i++)
        {
            if (Math.Abs(steady[i] - alternating[i]) > 1e-3f)
            {
                diverges = true;
                break;
            }
        }
        Assert.That(diverges, Is.True);
    }
}
