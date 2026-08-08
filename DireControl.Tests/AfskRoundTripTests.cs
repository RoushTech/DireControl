using DireControl.Modem;
using DireControl.Modem.Ax25;
using DireControl.Modem.Dsp;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Full-chain modem tests: AX.25 frames are AFSK-modulated, optionally
/// degraded (noise, level changes, sample-clock skew, DC offset), and fed
/// through the receive pipeline.  Decode-rate floors act as a tuning
/// regression guard — parameter changes must never silently reduce them.
/// </summary>
[TestFixture]
public sealed class AfskRoundTripTests
{
    private const int SampleRate = 48000;

    private static byte[] MakeTestFrame(int seed)
    {
        var info = $"!3518.00N/08508.00W-Round-trip test frame {seed} with some payload text.";
        return Ax25Encoder.EncodeUiFrame($"N4WR-{seed % 15 + 1}", info, "WIDE1-1,WIDE2-1");
    }

    private static List<byte[]> Demodulate(float[] audio)
    {
        var receiver = AfskReceiver.CreateStandard(SampleRate);
        var decoded = new List<byte[]>();
        receiver.FrameReceived += (frame, _) => decoded.Add(frame);

        // Feed in modest blocks like the live capture path does.
        for (var pos = 0; pos < audio.Length; pos += 1024)
            receiver.ProcessSamples(audio.AsSpan(pos, Math.Min(1024, audio.Length - pos)));

        // Flush with silence so trailing bits clock through the PLL/deframer.
        receiver.ProcessSamples(new float[SampleRate / 10]);
        return decoded;
    }

    [Test]
    public void CleanSignal_DecodesEveryFrame()
    {
        var modulator = new AfskModulator(SampleRate);

        for (var seed = 0; seed < 10; seed++)
        {
            var frame = MakeTestFrame(seed);
            var audio = modulator.GenerateFrame(frame);
            var decoded = Demodulate(audio);

            Assert.That(decoded, Has.Count.EqualTo(1), $"frame {seed} did not decode");
            Assert.That(decoded[0], Is.EqualTo(frame).AsCollection, $"frame {seed} corrupted");
        }
    }

    [Test]
    public void CleanSignal_RoundTripsTnc2Content()
    {
        var frame = Ax25Encoder.EncodeUiFrame("KM4ABC-9", ":N4WR     :hello direwolf-free world{42", "WIDE1-1");
        var audio = new AfskModulator(SampleRate).GenerateFrame(frame);
        var decoded = Demodulate(audio);

        Assert.That(decoded, Has.Count.EqualTo(1));
        Assert.That(Ax25Decoder.TryDecode(decoded[0], out var parsed), Is.True);
        Assert.That(parsed.ToTnc2(), Is.EqualTo("KM4ABC-9>APRS,WIDE1-1::N4WR     :hello direwolf-free world{42"));
    }

    [TestCase(0.25f, Description = "Quiet input")]
    [TestCase(1.0f, Description = "Full-scale input")]
    public void LevelVariations_StillDecode(float gain)
    {
        var frame = MakeTestFrame(3);
        var audio = new AfskModulator(SampleRate).GenerateFrame(frame, amplitude: 0.8f * gain);
        var decoded = Demodulate(audio);

        Assert.That(decoded, Has.Count.EqualTo(1));
        Assert.That(decoded[0], Is.EqualTo(frame).AsCollection);
    }

    [Test]
    public void DcOffset_StillDecodes()
    {
        var frame = MakeTestFrame(4);
        var audio = new AfskModulator(SampleRate).GenerateFrame(frame);
        for (var i = 0; i < audio.Length; i++)
            audio[i] += 0.1f;

        var decoded = Demodulate(audio);
        Assert.That(decoded, Has.Count.EqualTo(1));
    }

    [TestCase(0.05f, 20, Description = "Light noise — all frames must decode")]
    [TestCase(0.15f, 18, Description = "Moderate noise — at least 90%")]
    public void WhiteNoise_DecodeRateFloor(float noiseAmplitude, int minDecoded)
    {
        var modulator = new AfskModulator(SampleRate);
        var rng = new Random(1234); // deterministic corpus
        var decodedCount = 0;

        for (var seed = 0; seed < 20; seed++)
        {
            var frame = MakeTestFrame(seed);
            var audio = modulator.GenerateFrame(frame, amplitude: 0.5f);
            for (var i = 0; i < audio.Length; i++)
                audio[i] += (float)(rng.NextDouble() * 2 - 1) * noiseAmplitude;

            var decoded = Demodulate(audio);
            if (decoded.Count == 1 && decoded[0].AsSpan().SequenceEqual(frame))
                decodedCount++;
        }

        Assert.That(decodedCount, Is.GreaterThanOrEqualTo(minDecoded),
            $"decoded {decodedCount}/20 at noise {noiseAmplitude}");
    }

    [TestCase(1195f, 2192f, Description = "Sender tones ~0.4% low")]
    [TestCase(1206f, 2211f, Description = "Sender tones ~0.5% high")]
    public void ToneFrequencySkew_StillDecodes(float mark, float space)
    {
        var frame = MakeTestFrame(7);
        var audio = new AfskModulator(SampleRate, markFreq: mark, spaceFreq: space).GenerateFrame(frame);
        var decoded = Demodulate(audio);

        Assert.That(decoded, Has.Count.EqualTo(1));
        Assert.That(decoded[0], Is.EqualTo(frame).AsCollection);
    }

    [TestCase(1194f, Description = "Sender baud 0.5% slow")]
    [TestCase(1206f, Description = "Sender baud 0.5% fast")]
    public void BaudRateSkew_StillDecodes(float senderBaud)
    {
        var frame = MakeTestFrame(9);
        var audio = new AfskModulator(SampleRate, baud: senderBaud).GenerateFrame(frame);
        var decoded = Demodulate(audio);

        Assert.That(decoded, Has.Count.EqualTo(1));
        Assert.That(decoded[0], Is.EqualTo(frame).AsCollection);
    }

    [Test]
    public void BackToBackFrames_AllDecode()
    {
        var modulator = new AfskModulator(SampleRate);
        var frames = Enumerable.Range(0, 5).Select(MakeTestFrame).ToList();

        var all = new List<float>();
        foreach (var frame in frames)
        {
            all.AddRange(modulator.GenerateFrame(frame));
            all.AddRange(new float[SampleRate / 20]); // 50 ms gap
        }

        var decoded = Demodulate([.. all]);
        Assert.That(decoded, Has.Count.EqualTo(frames.Count));
        for (var i = 0; i < frames.Count; i++)
            Assert.That(decoded[i], Is.EqualTo(frames[i]).AsCollection, $"frame {i} mismatch");
    }
}
