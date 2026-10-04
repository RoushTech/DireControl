using System.Buffers.Binary;
using System.Text;

namespace DireControl.Modem.Audio;

/// <summary>
/// Minimal RIFF/WAVE reader and writer for modem audio capture.
///
/// Captures exist so off-air audio can be replayed through the real
/// demodulator, so the format is deliberately lossless and full-rate: 16-bit
/// PCM at the modem's own sample rate.  Reading accepts 16-bit PCM and 32-bit
/// IEEE float, mono or multi-channel (channel 0 is taken), because recordings
/// may also arrive from other tools.
/// </summary>
public static class WaveFile
{
    private const int FormatPcm = 1;
    private const int FormatIeeeFloat = 3;
    private const int HeaderBytes = 44;

    /// <summary>Audio read from a wave file, as mono float samples in −1..1.</summary>
    public sealed record Audio(float[] Samples, int SampleRate)
    {
        public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
    }

    /// <summary>
    /// A wave file's shape, read from its header alone.  Used to describe a
    /// capture without loading megabytes of audio to do it.
    /// </summary>
    public sealed record Info(int SampleRate, int Channels, int BitsPerSample, long SampleFrames)
    {
        public TimeSpan Duration => SampleRate > 0
            ? TimeSpan.FromSeconds((double)SampleFrames / SampleRate)
            : TimeSpan.Zero;
    }

    /// <summary>Where the audio lives in the stream, and in what format.</summary>
    private readonly record struct ChunkLayout(
        int Format, int Channels, int SampleRate, int BitsPerSample, int DataBytes);

    /// <summary>
    /// Writes mono float samples as a 16-bit PCM wave file.  Samples outside
    /// −1..1 are clamped rather than allowed to wrap.
    /// </summary>
    public static void Write(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        Write(stream, samples, sampleRate);
    }

    public static void Write(Stream stream, ReadOnlySpan<float> samples, int sampleRate)
    {
        var dataBytes = samples.Length * 2;
        var header = new byte[HeaderBytes];

        "RIFF"u8.CopyTo(header.AsSpan(0));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), HeaderBytes - 8 + dataBytes);
        "WAVE"u8.CopyTo(header.AsSpan(8));

        "fmt "u8.CopyTo(header.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), FormatPcm);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), sampleRate * 2); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), 2); // block align
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16); // bits per sample

        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), dataBytes);

        stream.Write(header);

        // Converted in one pass and written in one call — a capture is millions
        // of samples, and writing them individually is needlessly slow.
        var pcm = new byte[dataBytes];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), value);
        }
        stream.Write(pcm);
    }

    /// <summary>
    /// Reads a wave file as mono float samples.  Multi-channel files are
    /// reduced to channel 0 rather than mixed, since a mixdown would not be a
    /// faithful replay of what the modem heard.
    /// </summary>
    public static Audio Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        return Read(stream);
    }

    public static Audio Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        var layout = ReadLayout(reader, stream);
        var samples = ReadSamples(
            reader, layout.DataBytes, layout.Format, layout.Channels, layout.BitsPerSample);
        return new Audio(samples, layout.SampleRate);
    }

    /// <summary>Reads only the header, without loading the audio.</summary>
    public static Info ReadInfo(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        return ReadInfo(stream);
    }

    public static Info ReadInfo(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        var layout = ReadLayout(reader, stream);

        var frameBytes = layout.BitsPerSample / 8 * layout.Channels;
        return new Info(
            layout.SampleRate,
            layout.Channels,
            layout.BitsPerSample,
            frameBytes > 0 ? layout.DataBytes / frameBytes : 0);
    }

    /// <summary>
    /// Walks the chunk list and leaves the stream positioned at the start of
    /// the audio data.
    /// </summary>
    private static ChunkLayout ReadLayout(BinaryReader reader, Stream stream)
    {
        if (ReadFourCc(reader) != "RIFF")
            throw new InvalidDataException("Not a RIFF file.");
        reader.ReadInt32(); // overall size, not needed
        if (ReadFourCc(reader) != "WAVE")
            throw new InvalidDataException("RIFF file is not WAVE.");

        int format = 0, channels = 0, sampleRate = 0, bitsPerSample = 0;
        var haveFormat = false;

        // Walk the chunk list rather than assuming a canonical 44-byte header —
        // real files carry LIST/fact/metadata chunks before the audio.
        while (stream.Position < stream.Length)
        {
            var chunkId = ReadFourCc(reader);
            var chunkSize = reader.ReadInt32();
            if (chunkSize < 0)
                throw new InvalidDataException($"Chunk \"{chunkId}\" has a negative size.");

            if (chunkId == "fmt ")
            {
                format = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // byte rate
                reader.ReadInt16(); // block align
                bitsPerSample = reader.ReadInt16();
                haveFormat = true;

                // Skip any format extension (WAVE_FORMAT_EXTENSIBLE and friends).
                const int consumed = 16;
                if (chunkSize > consumed)
                    stream.Seek(chunkSize - consumed, SeekOrigin.Current);
            }
            else if (chunkId == "data")
            {
                if (!haveFormat)
                    throw new InvalidDataException("WAVE data chunk appeared before its format.");

                // A data size that overruns the file (a truncated capture) is
                // clamped so a partial recording still replays.
                var available = (int)Math.Min(chunkSize, stream.Length - stream.Position);
                return new ChunkLayout(format, channels, sampleRate, bitsPerSample, available);
            }
            else
            {
                stream.Seek(chunkSize, SeekOrigin.Current);
            }

            // Chunks are word-aligned; an odd size carries a pad byte.
            if (chunkSize % 2 != 0 && stream.Position < stream.Length)
                stream.Seek(1, SeekOrigin.Current);
        }

        throw new InvalidDataException("WAVE file has no data chunk.");
    }

    private static float[] ReadSamples(
        BinaryReader reader, int dataBytes, int format, int channels, int bitsPerSample)
    {
        if (channels < 1)
            throw new InvalidDataException($"WAVE file reports {channels} channels.");

        var bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample < 1)
            throw new InvalidDataException($"WAVE file reports {bitsPerSample} bits per sample.");

        var frameBytes = bytesPerSample * channels;
        var frames = dataBytes / frameBytes;
        var samples = new float[frames];

        var raw = reader.ReadBytes(frames * frameBytes);
        for (var frame = 0; frame < frames; frame++)
        {
            // Channel 0 only; later channels are skipped over.
            var offset = frame * frameBytes;
            samples[frame] = (format, bitsPerSample) switch
            {
                (FormatPcm, 16) => BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(offset))
                    / (float)short.MaxValue,
                (FormatPcm, 8) => (raw[offset] - 128) / 128f,
                (FormatIeeeFloat, 32) => BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(offset)),
                _ => throw new InvalidDataException(
                    $"Unsupported WAVE format {format} at {bitsPerSample} bits per sample."),
            };
        }

        return samples;
    }

    private static string ReadFourCc(BinaryReader reader) =>
        Encoding.ASCII.GetString(reader.ReadBytes(4));
}
