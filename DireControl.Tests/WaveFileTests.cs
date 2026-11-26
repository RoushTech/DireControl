using DireControl.Modem.Audio;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Captured audio is only useful if it replays as exactly what the modem heard,
/// so the wave codec has to be lossless within 16-bit quantisation and has to
/// survive the chunk layouts real files use.
/// </summary>
[TestFixture]
public sealed class WaveFileTests
{
    private const int SampleRate = 48000;

    private static float[] Tone(int samples)
    {
        var audio = new float[samples];
        for (var i = 0; i < samples; i++)
            audio[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1200f * i / SampleRate);
        return audio;
    }

    [Test]
    public void RoundTrip_PreservesSamplesAndRate()
    {
        var original = Tone(4800);
        using var stream = new MemoryStream();

        WaveFile.Write(stream, original, SampleRate);
        stream.Position = 0;
        var read = WaveFile.Read(stream);

        Assert.That(read.SampleRate, Is.EqualTo(SampleRate));
        Assert.That(read.Samples, Has.Length.EqualTo(original.Length));
        // 16-bit quantisation is the only loss permitted.
        for (var i = 0; i < original.Length; i++)
            Assert.That(read.Samples[i], Is.EqualTo(original[i]).Within(1f / short.MaxValue));
    }

    [Test]
    public void RoundTrip_ReportsDuration()
    {
        using var stream = new MemoryStream();
        WaveFile.Write(stream, new float[SampleRate * 3], SampleRate);
        stream.Position = 0;

        Assert.That(WaveFile.Read(stream).Duration.TotalSeconds, Is.EqualTo(3).Within(0.001));
    }

    [Test]
    public void Write_ClampsRatherThanWrapping()
    {
        using var stream = new MemoryStream();
        WaveFile.Write(stream, [4f, -4f, 0f], SampleRate);
        stream.Position = 0;

        var read = WaveFile.Read(stream);

        // Wrapping would flip these to large opposite-sign values, turning
        // clipping into noise and hiding it from the replay report.
        Assert.That(read.Samples[0], Is.EqualTo(1f).Within(0.001f));
        Assert.That(read.Samples[1], Is.EqualTo(-1f).Within(0.001f));
        Assert.That(read.Samples[2], Is.EqualTo(0f).Within(0.001f));
    }

    [Test]
    public void Read_SkipsUnknownChunksBeforeData()
    {
        using var source = new MemoryStream();
        WaveFile.Write(source, Tone(960), SampleRate);
        var canonical = source.ToArray();

        // Splice a LIST chunk between "fmt " and "data", as editors emit.
        using var spliced = new MemoryStream();
        spliced.Write(canonical.AsSpan(0, 36)); // RIFF + fmt
        spliced.Write("LIST"u8);
        spliced.Write(BitConverter.GetBytes(4));
        spliced.Write("INFO"u8);
        spliced.Write(canonical.AsSpan(36)); // data chunk onwards
        spliced.Position = 0;

        var read = WaveFile.Read(spliced);

        Assert.That(read.SampleRate, Is.EqualTo(SampleRate));
        Assert.That(read.Samples, Has.Length.EqualTo(960));
    }

    [Test]
    public void Read_TakesChannelZeroOfAStereoFile()
    {
        // Hand-built stereo: left ramps up, right is silent. A mixdown would
        // halve the left channel, which would not be what the modem heard.
        using var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + 16);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)2); // stereo
        writer.Write(SampleRate);
        writer.Write(SampleRate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(16);
        for (short i = 1; i <= 4; i++)
        {
            writer.Write((short)(i * 8000)); // left
            writer.Write((short)0); // right
        }
        stream.Position = 0;

        var read = WaveFile.Read(stream);

        Assert.That(read.Samples, Has.Length.EqualTo(4));
        Assert.That(read.Samples[0], Is.EqualTo(8000f / short.MaxValue).Within(0.001f));
        Assert.That(read.Samples[3], Is.EqualTo(32000f / short.MaxValue).Within(0.001f));
    }

    [Test]
    public void ReadInfo_DescribesTheFileWithoutLoadingAudio()
    {
        using var stream = new MemoryStream();
        WaveFile.Write(stream, new float[SampleRate * 2], SampleRate);
        stream.Position = 0;

        var info = WaveFile.ReadInfo(stream);

        // The capture listing reads duration from here rather than assuming a
        // sample rate it does not own.
        Assert.That(info.SampleRate, Is.EqualTo(SampleRate));
        Assert.That(info.Channels, Is.EqualTo(1));
        Assert.That(info.BitsPerSample, Is.EqualTo(16));
        Assert.That(info.SampleFrames, Is.EqualTo(SampleRate * 2));
        Assert.That(info.Duration.TotalSeconds, Is.EqualTo(2).Within(0.001));
    }

    [Test]
    public void Read_RecoversAudioFromATruncatedFile()
    {
        using var source = new MemoryStream();
        WaveFile.Write(source, Tone(4800), SampleRate);
        var full = source.ToArray();

        // A capture killed mid-write has a data size claiming more than the
        // file holds; it must still replay what did land.
        using var truncated = new MemoryStream(full.AsSpan(0, full.Length - 2000).ToArray());
        var read = WaveFile.Read(truncated);

        Assert.That(read.SampleRate, Is.EqualTo(SampleRate));
        Assert.That(read.Samples, Has.Length.EqualTo(4800 - 1000));
    }

    [Test]
    public void Read_RejectsNonRiffData()
    {
        using var stream = new MemoryStream(new byte[64]);
        Assert.Throws<InvalidDataException>(() => WaveFile.Read(stream));
    }
}
