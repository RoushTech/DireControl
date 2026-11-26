using DireControl.Modem;
using DireControl.Modem.Ax25;
using DireControl.Modem.Dsp;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Preamble detection is what tells a real transmission from channel noise.
/// Audio level cannot do it: with the radio's squelch open, noise sits at a
/// similar level to a signal, and carrier detect is known to arm on noise.  A
/// run of consecutive opening flags is the discriminator, so the automatic
/// audio capture and the channel-activity verdict both key off it.
/// </summary>
[TestFixture]
public sealed class PreambleDetectionTests
{
    private const int SampleRate = 48000;

    private static long Preambles(AfskReceiver receiver) =>
        receiver.Demodulators.Sum(d => d.PreambleCount);

    [Test]
    public void RealTransmission_RegistersAPreamble()
    {
        var frame = Ax25Encoder.EncodeUiFrame("N4WR-1", "!3518.00N/08508.00W-preamble test", "WIDE1-1");
        var audio = new AfskModulator(SampleRate).GenerateFrame(frame);

        var receiver = AfskReceiver.CreateStandard(SampleRate);
        receiver.ProcessSamples(audio);

        Assert.That(Preambles(receiver), Is.GreaterThan(0));
        Assert.That(receiver.ValidFrameCount, Is.EqualTo(1));
    }

    [Test]
    public void BandNoise_ProducesNoPreambleEvenWhenCarrierDetectArms()
    {
        // Carrier detect is known to false-arm on noise — that is precisely why
        // the capture trigger cannot rely on it. This test exists to guarantee
        // the preamble counter does not share the weakness.
        var random = new Random(20261005);
        var noise = new float[SampleRate * 5];
        for (var i = 0; i < noise.Length; i++)
            noise[i] = (float)(random.NextDouble() * 2 - 1) * 0.25f;

        var receiver = AfskReceiver.CreateStandard(SampleRate);
        receiver.ProcessSamples(noise);

        Assert.That(Preambles(receiver), Is.Zero, "band noise must not look like a transmission");
        Assert.That(receiver.ValidFrameCount, Is.Zero);
    }

    [Test]
    public void LoudNoiseAndQuietNoise_BothRegisterNoPreamble()
    {
        // Opening the radio's squelch raises the noise floor enormously. The
        // discriminator must be unaffected by that, which an audio-level
        // threshold would not be.
        foreach (var amplitude in new[] { 0.01f, 0.5f })
        {
            var random = new Random(7);
            var noise = new float[SampleRate * 3];
            for (var i = 0; i < noise.Length; i++)
                noise[i] = (float)(random.NextDouble() * 2 - 1) * amplitude;

            var receiver = AfskReceiver.CreateStandard(SampleRate);
            receiver.ProcessSamples(noise);

            Assert.That(
                Preambles(receiver), Is.Zero, $"noise at amplitude {amplitude} looked like a preamble");
        }
    }

    [Test]
    public void AQuietTransmission_StillRegistersAPreamble()
    {
        // The counterpart risk: a level-based gate would discard weak but real
        // transmissions. Preamble detection must not.
        var frame = Ax25Encoder.EncodeUiFrame("N4WR-1", "quiet but real", null);
        var audio = new AfskModulator(SampleRate).GenerateFrame(frame);
        for (var i = 0; i < audio.Length; i++)
            audio[i] *= 0.05f;

        var receiver = AfskReceiver.CreateStandard(SampleRate);
        receiver.ProcessSamples(audio);

        Assert.That(Preambles(receiver), Is.GreaterThan(0));
    }
}
