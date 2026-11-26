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

    /// <summary>
    /// Renders an arbitrary pre-NRZI bit stream as phase-continuous AFSK.
    /// </summary>
    internal static float[] RenderBits(IReadOnlyList<bool> bits, float amplitude = 0.5f)
    {
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
            boundary += samplesPerBit;
            while (samples.Count < (int)Math.Round(boundary))
            {
                samples.Add((float)(Math.Sin(phase) * amplitude));
                phase += 2 * Math.PI * freq / SampleRate;
                if (phase > 2 * Math.PI)
                    phase -= 2 * Math.PI;
            }
        }
        return [.. samples];
    }

    /// <summary>
    /// A transmission the way many TNCs and digipeaters send it: TXDelay filled
    /// with zero bits (alternating tones), then only two flags before the frame.
    /// </summary>
    internal static List<bool> ZeroFillTransmission(byte[] frame, int zeroBits = 160)
    {
        var bits = new List<bool>(Enumerable.Repeat(false, zeroBits));
        bits.AddRange(AfskModulator.BuildHdlcBitStream(frame, leadFlags: 2, tailFlags: 2));
        return bits;
    }

    [Test]
    public void ZeroFillPreamble_ArmsCarrierDetectBeforeTheFrameAndHoldsThroughIt()
    {
        // A long frame, so the zero-fill's own half-second hold would expire
        // mid-frame if the two flags after it did not qualify the frame.
        var frame = Ax25Encoder.EncodeUiFrame("N4WR-1", new string('x', 220), "WIDE1-1,WIDE2-1");
        var bits = ZeroFillTransmission(frame);
        var audio = RenderBits(bits);
        var samplesPerBit = SampleRate / 1200;

        var receiver = AfskReceiver.CreateStandard(SampleRate);
        var decoded = 0;
        receiver.FrameReceived += (_, _) => decoded++;

        var preambleEnd = 160 * samplesPerBit;
        var frameEnd = audio.Length - 2 * 8 * samplesPerBit;
        var armedDuringPreamble = false;
        var droppedMidFrame = false;
        for (var pos = 0; pos < audio.Length; pos += 256)
        {
            receiver.ProcessSamples(audio.AsSpan(pos, Math.Min(256, audio.Length - pos)));
            if (pos < preambleEnd && receiver.CarrierDetected)
                armedDuringPreamble = true;
            if (pos >= preambleEnd && pos < frameEnd && !receiver.CarrierDetected)
                droppedMidFrame = true;
        }
        receiver.ProcessSamples(new float[SampleRate / 10]);

        Assert.Multiple(() =>
        {
            Assert.That(armedDuringPreamble, Is.True, "a zero-fill preamble is a carrier");
            Assert.That(droppedMidFrame, Is.False, "carrier must hold from the preamble to the closing flag");
            Assert.That(decoded, Is.EqualTo(1));
        });
    }

    [Test]
    public void ZeroFillPreamble_CountsAsOnePreamble()
    {
        var frame = Ax25Encoder.EncodeUiFrame("N4WR-1", "!3518.00N/08508.00W-zero fill", "WIDE1-1");
        var receiver = AfskReceiver.CreateStandard(SampleRate);
        receiver.ProcessSamples(RenderBits(ZeroFillTransmission(frame)));
        receiver.ProcessSamples(new float[SampleRate / 10]);

        Assert.That(receiver.Demodulators.Sum(d => d.PreambleCount), Is.EqualTo(1));
        Assert.That(receiver.ValidFrameCount, Is.EqualTo(1));
    }

    [Test]
    public void ZeroFillThenLongFlagRun_IsStillOnePreamble()
    {
        var frame = Ax25Encoder.EncodeUiFrame("N4WR-1", "!3518.00N/08508.00W-both", "WIDE1-1");
        var bits = new List<bool>(Enumerable.Repeat(false, 120));
        bits.AddRange(AfskModulator.BuildHdlcBitStream(frame, leadFlags: 20, tailFlags: 2));

        var receiver = AfskReceiver.CreateStandard(SampleRate);
        receiver.ProcessSamples(RenderBits(bits));
        receiver.ProcessSamples(new float[SampleRate / 10]);

        Assert.That(receiver.Demodulators.Sum(d => d.PreambleCount), Is.EqualTo(1));
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
