using DireControl.Modem;
using DireControl.Modem.Ax25;
using DireControl.Modem.Dsp;
using DireControl.Modem.Hdlc;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// FCS-guided bit repair: a frame that fails its checksum is retried with its
/// least-confident bit-cell decisions inverted.  A wrong tone decision in one
/// bit cell inverts two adjacent NRZI-decoded bits, which is what these tests
/// inject.
/// </summary>
[TestFixture]
public sealed class BitRepairTests
{
    private const int SampleRate = 48000;
    private const int LeadFlags = 4;

    private static byte[] MakeFrame(int seed = 1) =>
        Ax25Encoder.EncodeUiFrame(
            $"N4WR-{seed % 15 + 1}",
            $"!3518.00N/08508.00W-Bit repair test frame {seed} with some payload text.",
            "WIDE1-1,WIDE2-1");

    private sealed record Result(List<byte[]> Frames, List<int> RepairedBits, HdlcDeframer Deframer);

    /// <summary>
    /// Feeds <paramref name="frame"/> through a deframer with the bit cells at
    /// <paramref name="badCells"/> (offsets into the frame's stuffed bits)
    /// decided wrongly and reported with low confidence.
    /// </summary>
    private static Result Run(byte[] frame, int maxRepairBits, params int[] badCells)
    {
        var bits = AfskModulator.BuildHdlcBitStream(frame, LeadFlags, 2);
        var confidence = Enumerable.Repeat(1f, bits.Count).ToArray();
        foreach (var cell in badCells)
        {
            var index = LeadFlags * 8 + cell;
            bits[index] = !bits[index];
            bits[index + 1] = !bits[index + 1];
            confidence[index] = 0.01f;
        }

        var deframer = new HdlcDeframer { MaxRepairBits = maxRepairBits };
        var frames = new List<byte[]>();
        var repaired = new List<int>();
        deframer.FrameReceived += f =>
        {
            frames.Add(f);
            repaired.Add(deframer.LastFrameRepairedBits);
        };
        for (var i = 0; i < bits.Count; i++)
            deframer.ProcessBit(bits[i], confidence[i]);

        return new Result(frames, repaired, deframer);
    }

    [Test]
    public void CleanFrame_IsNotReportedAsRepaired()
    {
        var frame = MakeFrame();
        var result = Run(frame, maxRepairBits: 1);

        Assert.That(result.Frames, Has.Count.EqualTo(1));
        Assert.That(result.Frames[0], Is.EqualTo(frame).AsCollection);
        Assert.That(result.RepairedBits, Is.EqualTo(new[] { 0 }));
        Assert.That(result.Deframer.RepairedFrameCount, Is.Zero);
    }

    [TestCase(40)]
    [TestCase(203)]
    [TestCase(411)]
    public void OneBadCell_IsRepairedAndReported(int badCell)
    {
        var frame = MakeFrame();
        var result = Run(frame, maxRepairBits: 1, badCell);

        Assert.That(result.Frames, Has.Count.EqualTo(1), "the damaged frame should have been recovered");
        Assert.That(result.Frames[0], Is.EqualTo(frame).AsCollection);
        Assert.That(result.RepairedBits, Is.EqualTo(new[] { 1 }));
        Assert.That(result.Deframer.RepairedFrameCount, Is.EqualTo(1));
        Assert.That(result.Deframer.InvalidFrameCount, Is.Zero, "a recovered frame is not a CRC failure");
    }

    [Test]
    public void RepairOff_DropsTheDamagedFrame()
    {
        var result = Run(MakeFrame(), maxRepairBits: 0, 203);

        Assert.That(result.Frames, Is.Empty);
        Assert.That(result.Deframer.RepairedFrameCount, Is.Zero);
    }

    [Test]
    public void TwoBadCells_NeedTwoBitRepair()
    {
        var frame = MakeFrame();

        var single = Run(frame, maxRepairBits: 1, 120, 333);
        Assert.That(single.Frames, Is.Empty, "one-bit repair cannot fix two bad cells");

        var both = Run(frame, maxRepairBits: 2, 120, 333);
        Assert.That(both.Frames, Has.Count.EqualTo(1));
        Assert.That(both.Frames[0], Is.EqualTo(frame).AsCollection);
        Assert.That(both.RepairedBits, Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public void DamageBeyondTheCandidates_IsNotRepaired()
    {
        // Three bad cells exceed even two-bit repair.
        var result = Run(MakeFrame(), maxRepairBits: 2, 90, 250, 400);

        Assert.That(result.Frames, Is.Empty);
    }

    [Test]
    public void FrameWithoutAx25Addresses_IsNeverRepaired()
    {
        // FCS-valid as sent, but not AX.25: a repaired frame must also carry a
        // well-formed address field, since trying several bit patterns
        // multiplies the chance of an accidental FCS match.
        var frame = Enumerable.Range(0, 40).Select(i => (byte)(0x41 + i)).ToArray();

        Assert.That(Run(frame, maxRepairBits: 1).Frames, Has.Count.EqualTo(1), "valid as received");
        Assert.That(Run(frame, maxRepairBits: 1, 150).Frames, Is.Empty);
    }

    [Test]
    public void PlausibleAddressField_AcceptsRealFramesAndRejectsJunk()
    {
        Assert.That(HdlcDeframer.HasPlausibleAddressField(MakeFrame()), Is.True);
        Assert.That(
            HdlcDeframer.HasPlausibleAddressField(Ax25Encoder.EncodeUiFrame("N4WR", "no path", null)), Is.True);

        var random = new Random(5);
        var junk = new byte[60];
        for (var i = 0; i < 2000; i++)
        {
            random.NextBytes(junk);
            Assert.That(HdlcDeframer.HasPlausibleAddressField(junk), Is.False);
        }
    }

    // ── Through the whole receiver ────────────────────────────────────────────

    private static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    /// <summary>Decodes noisy copies of known frames; returns (right, wrong, repaired).</summary>
    private static (int Right, int Wrong, int Repaired) DecodeInNoise(int maxRepairBits, double sigma, int frames)
    {
        var modulator = new AfskModulator(SampleRate);
        var rng = new Random(20261121);
        int right = 0, wrong = 0, repaired = 0;

        for (var i = 0; i < frames; i++)
        {
            var frame = MakeFrame(i);
            var audio = modulator.GenerateFrame(frame, amplitude: 0.5f);
            for (var s = 0; s < audio.Length; s++)
                audio[s] += (float)(Gaussian(rng) * sigma);

            var receiver = AfskReceiver.CreateStandard(SampleRate, maxRepairBits);
            receiver.FrameReceived += (f, decode) =>
            {
                if (f.AsSpan().SequenceEqual(frame))
                    right++;
                else
                    wrong++;
                if (decode.RepairedBits > 0)
                    repaired++;
            };
            for (var pos = 0; pos < audio.Length; pos += 1024)
                receiver.ProcessSamples(audio.AsSpan(pos, Math.Min(1024, audio.Length - pos)));
            receiver.ProcessSamples(new float[SampleRate / 10]);
        }

        return (right, wrong, repaired);
    }

    [Test]
    public void CleanSignal_IsNeverMarkedRepaired()
    {
        var (right, wrong, repaired) = DecodeInNoise(maxRepairBits: 2, sigma: 0, frames: 10);

        Assert.That(right, Is.EqualTo(10));
        Assert.That(wrong, Is.Zero);
        Assert.That(repaired, Is.Zero);
    }

    [Test]
    public void MarginalSignal_RepairRecoversFramesWithoutInventingAny()
    {
        // Noise set so the plain demodulator loses most frames.
        const double sigma = 0.52;
        const int frames = 60;

        var off = DecodeInNoise(0, sigma, frames);
        var one = DecodeInNoise(1, sigma, frames);
        var two = DecodeInNoise(2, sigma, frames);
        TestContext.Out.WriteLine($"off {off}, one-bit {one}, two-bit {two}");

        Assert.That(off.Repaired, Is.Zero);
        Assert.That(one.Right, Is.GreaterThanOrEqualTo(off.Right + 5), "one-bit repair should recover frames");
        Assert.That(one.Repaired, Is.EqualTo(one.Right - off.Right), "every extra frame is marked repaired");
        Assert.That(two.Right, Is.GreaterThanOrEqualTo(one.Right));
        Assert.That(one.Wrong, Is.Zero, "one-bit repair must not produce a frame that was never sent");
    }
}
