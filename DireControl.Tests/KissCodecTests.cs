using DireControl.Modem.Dsp;
using DireControl.Modem.Kiss;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>KISS framing round-trips, escaping, and the FFT sanity check.</summary>
[TestFixture]
public sealed class KissCodecTests
{
    private static List<(byte Command, byte Channel, byte[] Payload)> Decode(byte[] wire)
    {
        var decoder = new KissDecoder();
        var frames = new List<(byte, byte, byte[])>();
        decoder.FrameReceived += (cmd, chan, payload) => frames.Add((cmd, chan, payload));
        decoder.ProcessBytes(wire);
        return frames;
    }

    [Test]
    public void EncodeDecode_RoundTrips()
    {
        var payload = new byte[] { 0x01, 0x02, 0xC0, 0xDB, 0x03, 0xC0, 0xDB };
        var wire = KissCodec.EncodeDataFrame(channel: 3, payload);

        var frames = Decode(wire);
        Assert.That(frames, Has.Count.EqualTo(1));
        Assert.That(frames[0].Command, Is.EqualTo(KissCodec.DataFrameCommand));
        Assert.That(frames[0].Channel, Is.EqualTo(3));
        Assert.That(frames[0].Payload, Is.EqualTo(payload).AsCollection);
    }

    [Test]
    public void Decoder_HandlesBackToBackAndSplitFrames()
    {
        var a = KissCodec.EncodeDataFrame(0, [0x11, 0x22]);
        var b = KissCodec.EncodeDataFrame(1, [0xC0]); // needs escaping
        var wire = a.Concat(b).ToArray();

        // Feed one byte at a time to exercise the streaming state machine.
        var decoder = new KissDecoder();
        var frames = new List<(byte, byte, byte[])>();
        decoder.FrameReceived += (cmd, chan, payload) => frames.Add((cmd, chan, payload));
        foreach (var by in wire)
            decoder.ProcessBytes([by]);

        Assert.That(frames, Has.Count.EqualTo(2));
        Assert.That(frames[0].Item3, Is.EqualTo(new byte[] { 0x11, 0x22 }).AsCollection);
        Assert.That(frames[1].Item2, Is.EqualTo(1));
        Assert.That(frames[1].Item3, Is.EqualTo(new byte[] { 0xC0 }).AsCollection);
    }

    [Test]
    public void Decoder_IgnoresKeepaliveFends()
    {
        Assert.That(Decode([KissCodec.Fend, KissCodec.Fend, KissCodec.Fend]), Is.Empty);
    }

    [Test]
    public void Decoder_ExtractsCommandAndChannelNibbles()
    {
        // Raw wire: FEND, type byte 0x53 (channel 5, cmd 3 = TXTAIL), data, FEND.
        var frames = Decode([KissCodec.Fend, 0x53, 0x0A, KissCodec.Fend]);
        Assert.That(frames, Has.Count.EqualTo(1));
        Assert.That(frames[0].Command, Is.EqualTo(3));
        Assert.That(frames[0].Channel, Is.EqualTo(5));
        Assert.That(frames[0].Payload, Is.EqualTo(new byte[] { 0x0A }).AsCollection);
    }

    // ── Spectrum sanity ──────────────────────────────────────────────────────

    [Test]
    public void SpectrumAnalyzer_PeaksAtToneFrequency()
    {
        const int sampleRate = 48000;
        var analyzer = new SpectrumAnalyzer(sampleRate);

        // 1 kHz sine at half scale.
        var samples = new float[SpectrumAnalyzer.FftSize * 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1000f * i / sampleRate);
        analyzer.AddSamples(samples);

        var spectrum = analyzer.ComputeSpectrum();
        Assert.That(spectrum, Is.Not.Null);
        Assert.That(spectrum!.Length, Is.EqualTo(analyzer.BinCount));

        var peakBin = Array.IndexOf(spectrum, spectrum.Max());
        var peakHz = peakBin * analyzer.BinWidthHz;
        Assert.That(peakHz, Is.EqualTo(1000).Within(50), "peak should sit at the tone frequency");

        // Peak clearly above the noise floor far from the tone.
        Assert.That(spectrum[peakBin], Is.GreaterThan(spectrum[analyzer.BinCount - 1] + 50));
    }

    [Test]
    public void SpectrumAnalyzer_NullUntilWindowFilled()
    {
        var analyzer = new SpectrumAnalyzer(48000);
        analyzer.AddSamples(new float[100]);
        Assert.That(analyzer.ComputeSpectrum(), Is.Null);
    }
}
