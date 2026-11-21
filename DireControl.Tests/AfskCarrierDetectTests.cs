using DireControl.Modem;
using DireControl.Modem.Ax25;
using DireControl.Modem.Dsp;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Data-carrier detect: a real transmission (preamble of back-to-back flags)
/// must arm DCD, while silence and band noise must not — lone noise-decoded
/// 0x7E patterns previously held "Carrier: detected" on a dead band.
/// </summary>
[TestFixture]
public sealed class AfskCarrierDetectTests
{
    private const int SampleRate = 48000;

    [Test]
    public void RealTransmission_ArmsCarrierDetect()
    {
        var frame = Ax25Encoder.EncodeUiFrame("N4WR-1", "!3518.00N/08508.00W-DCD test", "WIDE1-1");
        var audio = new AfskModulator(SampleRate).GenerateFrame(frame);

        var receiver = AfskReceiver.CreateStandard(SampleRate);
        receiver.ProcessSamples(audio);

        Assert.That(receiver.CarrierDetected, Is.True,
            "a modulated frame's flag preamble should arm DCD");
    }

    [Test]
    public void Silence_DoesNotArmCarrierDetect()
    {
        var receiver = AfskReceiver.CreateStandard(SampleRate);
        receiver.ProcessSamples(new float[SampleRate * 2]);

        Assert.That(receiver.CarrierDetected, Is.False, "silence must not arm DCD");
    }

    [Test]
    public void NoiseOnly_DoesNotHoldCarrierDetect()
    {
        // Deterministic band noise — the exact case from the field report: RF dead,
        // audio floor only, yet the old single-flag DCD kept showing "detected".
        var random = new Random(1234);
        var noise = new float[SampleRate * 3];
        for (var i = 0; i < noise.Length; i++)
            noise[i] = (float)(random.NextDouble() * 2 - 1) * 0.05f;

        var receiver = AfskReceiver.CreateStandard(SampleRate);
        receiver.ProcessSamples(noise);

        Assert.That(receiver.CarrierDetected, Is.False,
            "band noise must not hold DCD armed");
    }

    private static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    /// <summary>
    /// The false-arm rate never depended on level — it came from the weak
    /// two-flag rule — so it is checked across three orders of magnitude.
    /// </summary>
    [TestCase(0.001)]
    [TestCase(0.02)]
    [TestCase(0.1)]
    public void GaussianNoise_HoldsCarrierDetectUnderOnePercent(double rms)
    {
        var rng = new Random(20261121);
        var receiver = AfskReceiver.CreateStandard(SampleRate);
        var block = new float[1024];
        var blocks = SampleRate * 10 / block.Length;
        var carrierBlocks = 0;

        for (var b = 0; b < blocks; b++)
        {
            for (var i = 0; i < block.Length; i++)
                block[i] = (float)(Gaussian(rng) * rms);
            receiver.ProcessSamples(block);
            if (receiver.CarrierDetected)
                carrierBlocks++;
        }

        Assert.That(carrierBlocks / (double)blocks, Is.LessThan(0.01),
            $"carrier detect on for {carrierBlocks}/{blocks} blocks at noise rms {rms}");
    }

    /// <summary>
    /// A long frame outlasts the preamble's half-second DCD hold.  Carrier must
    /// stay asserted from the opening flags to the closing flag, or CSMA sees
    /// a clear channel and keys on top of the packet still arriving.
    /// </summary>
    [Test]
    public void LongFrame_HoldsCarrierDetectUntilClosingFlag()
    {
        var info = "!3518.00N/08508.00W-" + new string('x', 230);
        var frame = Ax25Encoder.EncodeUiFrame("N4WR-1", info, "WIDE1-1,WIDE2-1");
        Assert.That(frame.Length, Is.GreaterThanOrEqualTo(250));

        var modulator = new AfskModulator(SampleRate);
        var audio = modulator.GenerateTransmission([frame], leadFlags: 30, tailFlags: 3, amplitude: 0.5f);

        // Bit timeline: 30 flags, then the stuffed frame, then the tail.
        var samplesPerBit = SampleRate / 1200.0;
        var firstFlagPairEnd = (int)(3 * 8 * samplesPerBit) + 1024;
        var bits = AfskModulator.BuildHdlcBitStream(frame, 30, 3);
        var closingFlagEnd = (int)((bits.Count - 2 * 8) * samplesPerBit);

        var receiver = AfskReceiver.CreateStandard(SampleRate);
        var decoded = 0;
        receiver.FrameReceived += (_, _) => decoded++;

        const int blockSize = 256;
        for (var pos = 0; pos < audio.Length; pos += blockSize)
        {
            receiver.ProcessSamples(audio.AsSpan(pos, Math.Min(blockSize, audio.Length - pos)));
            var end = pos + blockSize;
            if (end >= firstFlagPairEnd && end <= closingFlagEnd)
                Assert.That(receiver.CarrierDetected, Is.True,
                    $"carrier dropped at {end * 1000 / SampleRate} ms of {closingFlagEnd * 1000 / SampleRate} ms");
        }

        receiver.ProcessSamples(new float[SampleRate / 10]);
        Assert.That(decoded, Is.EqualTo(1));
    }
}
