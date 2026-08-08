using DireControl.Modem;
using DireControl.Modem.Dsp;
using DireControl.Modem.Hdlc;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Bit-level tests for the HDLC deframer, CRC, and frame deduplication —
/// exercised without any DSP so failures point at the framing layer.
/// </summary>
[TestFixture]
public sealed class HdlcDeframerTests
{
    private static byte[] MakeFrame(int length, byte seed = 0x41)
    {
        var frame = new byte[length];
        for (var i = 0; i < length; i++)
            frame[i] = (byte)(seed + i % 26);
        return frame;
    }

    private static List<byte[]> RunBits(IEnumerable<bool> bits)
    {
        var deframer = new HdlcDeframer();
        var frames = new List<byte[]>();
        deframer.FrameReceived += frames.Add;
        foreach (var bit in bits)
            deframer.ProcessBit(bit);
        return frames;
    }

    [Test]
    public void Crc16Ccitt_KnownVector()
    {
        // Standard X.25/Kermit check value for ASCII "123456789".
        var fcs = Crc16Ccitt.ComputeFcs("123456789"u8);
        Assert.That(fcs, Is.EqualTo(0x906E));
    }

    [Test]
    public void Crc16Ccitt_ValidatesOwnFcs()
    {
        var frame = MakeFrame(30);
        var fcs = Crc16Ccitt.ComputeFcs(frame);
        var withFcs = frame.Concat([(byte)(fcs & 0xFF), (byte)(fcs >> 8)]).ToArray();

        Assert.That(Crc16Ccitt.IsFrameValid(withFcs), Is.True);
        withFcs[5] ^= 0x01;
        Assert.That(Crc16Ccitt.IsFrameValid(withFcs), Is.False);
    }

    [Test]
    public void Deframer_DecodesStuffedFrame()
    {
        // 0xFF bytes force bit stuffing after every five 1s.
        var frame = MakeFrame(20).Concat(Enumerable.Repeat((byte)0xFF, 8)).ToArray();
        var bits = AfskModulator.BuildHdlcBitStream(frame, leadFlags: 4, tailFlags: 2);

        var decoded = RunBits(bits);
        Assert.That(decoded, Has.Count.EqualTo(1));
        Assert.That(decoded[0], Is.EqualTo(frame).AsCollection);
    }

    [Test]
    public void Deframer_RejectsCorruptedFcs()
    {
        var frame = MakeFrame(25);
        var bits = AfskModulator.BuildHdlcBitStream(frame, 4, 2);

        // Flip one mid-frame data bit (after the 4 lead flags = 32 bits).
        bits[40] = !bits[40];

        var deframer = new HdlcDeframer();
        var frames = new List<byte[]>();
        deframer.FrameReceived += frames.Add;
        foreach (var bit in bits)
            deframer.ProcessBit(bit);

        Assert.That(frames, Is.Empty);
        Assert.That(deframer.InvalidFrameCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void Deframer_RejectsTooShortFrames()
    {
        // 10 bytes is below the 15-byte AX.25 minimum (plus FCS).
        var bits = AfskModulator.BuildHdlcBitStream(MakeFrame(10), 4, 2);
        Assert.That(RunBits(bits), Is.Empty);
    }

    [Test]
    public void Deframer_RecoversAfterAbort()
    {
        var good = MakeFrame(20);
        var bits = new List<bool>();

        // A partial frame killed by an abort sequence (eight 1s)...
        bits.AddRange(AfskModulator.BuildHdlcBitStream(MakeFrame(20, 0x5A), 2, 0).Take(60));
        bits.AddRange(Enumerable.Repeat(true, 8));
        // ...followed by a clean frame.
        bits.AddRange(AfskModulator.BuildHdlcBitStream(good, 4, 2));

        var decoded = RunBits(bits);
        Assert.That(decoded, Has.Count.EqualTo(1));
        Assert.That(decoded[0], Is.EqualTo(good).AsCollection);
    }

    [Test]
    public void Deframer_BackToBackFramesSharedFlags()
    {
        var a = MakeFrame(20, 0x41);
        var b = MakeFrame(24, 0x61);

        var bits = new List<bool>();
        bits.AddRange(AfskModulator.BuildHdlcBitStream(a, 4, 1));
        // b opens on the flag that closed a (plus its own lead flag from tail above).
        bits.AddRange(AfskModulator.BuildHdlcBitStream(b, 1, 2));

        var decoded = RunBits(bits);
        Assert.That(decoded, Has.Count.EqualTo(2));
        Assert.That(decoded[0], Is.EqualTo(a).AsCollection);
        Assert.That(decoded[1], Is.EqualTo(b).AsCollection);
    }

    [Test]
    public void FrameDeduper_SuppressesWithinWindowOnly()
    {
        var deduper = new FrameDeduper(TimeSpan.FromSeconds(2));
        var frame = MakeFrame(30);
        var other = MakeFrame(30, 0x55);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.That(deduper.IsNewFrame(frame, t0), Is.True);
        Assert.That(deduper.IsNewFrame(frame, t0.AddMilliseconds(500)), Is.False, "duplicate inside window");
        Assert.That(deduper.IsNewFrame(other, t0.AddMilliseconds(500)), Is.True, "different frame is not a dup");
        Assert.That(deduper.IsNewFrame(frame, t0.AddSeconds(3)), Is.True, "outside window is fresh");
    }
}
