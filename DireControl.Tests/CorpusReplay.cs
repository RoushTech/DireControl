using System.Text;
using DireControl.Modem;
using DireControl.Modem.Audio;
using DireControl.Modem.Ax25;

namespace DireControl.Tests;

/// <summary>What replaying one captured recording through the demodulator produced.</summary>
public sealed record ReplayResult
{
    public required string Name { get; init; }
    public required int SampleRate { get; init; }
    public required double DurationSeconds { get; init; }

    // ── Audio health ──
    public required float Peak { get; init; }
    public required float Rms { get; init; }
    public required double ClippedPercent { get; init; }
    public required float DcOffset { get; init; }

    // ── What the modem made of it ──
    public required double CarrierSeconds { get; init; }
    public required int CarrierBursts { get; init; }
    public required long ValidFrames { get; init; }
    public required long InvalidFrames { get; init; }
    public required IReadOnlyDictionary<string, long> ValidByProfile { get; init; }
    public required IReadOnlyDictionary<string, long> InvalidByProfile { get; init; }
    public required IReadOnlyList<string> DecodedLines { get; init; }

    /// <summary>A transmission was present but nothing came out of it.</summary>
    public bool HeardNothingDecoded => CarrierBursts > 0 && ValidFrames == 0;
}

/// <summary>
/// Replays captured off-air audio through the real demodulator.
///
/// <see cref="AfskReceiver.ProcessSamples"/> is a pure function of the sample
/// stream, so a recording replays bit-identically to what happened live.  That
/// makes a captured failure reproducible: change a filter or add a profile,
/// replay the same corpus, and compare decode counts instead of guessing.
///
/// Audio is fed in 1024-sample blocks to mirror the live capture loop exactly,
/// so block-boundary behaviour (AGC, PLL, carrier detect) matches production.
/// </summary>
public static class CorpusReplay
{
    /// <summary>Matches the block size the live capture loop reads from ALSA.</summary>
    public const int BlockSamples = 1024;

    /// <summary>Samples at or above this magnitude are counted as clipped.</summary>
    private const float ClipThreshold = 0.99f;

    public static ReplayResult Replay(string path) =>
        Replay(Path.GetFileName(path), WaveFile.Read(path));

    public static ReplayResult Replay(string name, WaveFile.Audio audio)
    {
        var receiver = AfskReceiver.CreateStandard(audio.SampleRate);

        var decoded = new List<string>();
        receiver.FrameReceived += (frame, profile) =>
        {
            var line = Ax25Decoder.TryDecode(frame, out var ax25)
                ? ax25.ToTnc2()
                : $"<CRC ok but undecodable, {frame.Length} bytes>";
            decoded.Add($"[{profile.Name}] {line}");
        };

        var (peak, rms, clippedPercent, dcOffset) = Measure(audio.Samples);

        var carrierBlocks = 0;
        var bursts = 0;
        var wasCarrier = false;
        for (var offset = 0; offset < audio.Samples.Length; offset += BlockSamples)
        {
            var length = Math.Min(BlockSamples, audio.Samples.Length - offset);
            receiver.ProcessSamples(audio.Samples.AsSpan(offset, length));

            var carrier = receiver.CarrierDetected;
            if (carrier)
            {
                carrierBlocks++;
                if (!wasCarrier)
                    bursts++;
            }
            wasCarrier = carrier;
        }

        return new ReplayResult
        {
            Name = name,
            SampleRate = audio.SampleRate,
            DurationSeconds = audio.Duration.TotalSeconds,
            Peak = peak,
            Rms = rms,
            ClippedPercent = clippedPercent,
            DcOffset = dcOffset,
            CarrierSeconds = (double)carrierBlocks * BlockSamples / audio.SampleRate,
            CarrierBursts = bursts,
            ValidFrames = receiver.ValidFrameCount,
            InvalidFrames = receiver.Demodulators.Sum(d => d.InvalidFrameCount),
            ValidByProfile = receiver.Demodulators.ToDictionary(
                d => d.Profile.Name, d => d.ValidFrameCount),
            InvalidByProfile = receiver.Demodulators.ToDictionary(
                d => d.Profile.Name, d => d.InvalidFrameCount),
            DecodedLines = decoded,
        };
    }

    private static (float Peak, float Rms, double ClippedPercent, float DcOffset) Measure(
        float[] samples)
    {
        if (samples.Length == 0)
            return (0f, 0f, 0d, 0f);

        var peak = 0f;
        double sumSquares = 0;
        double sum = 0;
        var clipped = 0;

        foreach (var sample in samples)
        {
            var magnitude = MathF.Abs(sample);
            if (magnitude > peak)
                peak = magnitude;
            if (magnitude >= ClipThreshold)
                clipped++;
            sumSquares += (double)sample * sample;
            sum += sample;
        }

        return (
            peak,
            (float)Math.Sqrt(sumSquares / samples.Length),
            100.0 * clipped / samples.Length,
            (float)(sum / samples.Length));
    }

    /// <summary>Renders one result as a human-readable block for the test log.</summary>
    public static string Format(ReplayResult result)
    {
        var report = new StringBuilder();
        report.AppendLine($"{result.Name}");
        report.AppendLine(
            $"  {result.DurationSeconds:F1}s @ {result.SampleRate} Hz | "
            + $"peak {result.Peak:F3} rms {result.Rms:F3} "
            + $"clipped {result.ClippedPercent:F2}% dc {result.DcOffset:+0.000;-0.000}");
        report.AppendLine(
            $"  carrier: {result.CarrierSeconds:F1}s over {result.CarrierBursts} burst(s)");

        foreach (var (profile, valid) in result.ValidByProfile.OrderBy(p => p.Key))
        {
            result.InvalidByProfile.TryGetValue(profile, out var invalid);
            report.AppendLine($"  profile {profile}: {valid} valid, {invalid} CRC failure(s)");
        }

        foreach (var line in result.DecodedLines)
            report.AppendLine($"    {line}");

        // The leading diagnosis is what makes a pile of captures reviewable.
        report.AppendLine($"  => {Diagnose(result)}");
        return report.ToString();
    }

    /// <summary>
    /// A first-pass read of what the numbers suggest.  Deliberately blunt — it
    /// is a prompt for a human to look closer, not a verdict.
    /// </summary>
    public static string Diagnose(ReplayResult result)
    {
        if (result.ClippedPercent > 1.0)
            return $"CLIPPING: {result.ClippedPercent:F1}% of samples at full scale — "
                + "input level is too high, reduce the radio's audio output or the capture gain.";
        if (result.Peak < 0.05f)
            return $"VERY LOW LEVEL: peak {result.Peak:F3} — "
                + "the demodulator has almost nothing to work with, raise the capture gain.";
        if (MathF.Abs(result.DcOffset) > 0.02f)
            return $"DC OFFSET {result.DcOffset:+0.000;-0.000} — a biased input can skew the comparator.";
        if (result.CarrierBursts == 0)
            return "no carrier ever armed — no HDLC flags found, so either there is no packet "
                + "traffic here or the audio is too far gone to find a preamble.";
        if (result.ValidFrames == 0)
            return $"HEARD BUT NOT DECODED: {result.CarrierBursts} burst(s), "
                + $"{result.InvalidFrames} CRC failure(s), 0 valid frames — this is the case worth tuning.";
        if (result.InvalidFrames > 0)
            return $"partial: {result.ValidFrames} decoded, {result.InvalidFrames} failed.";
        return $"clean: {result.ValidFrames} frame(s) decoded, no failures.";
    }

    /// <summary>Renders a whole corpus, with a summary line first.</summary>
    public static string FormatAll(IReadOnlyList<ReplayResult> results)
    {
        var report = new StringBuilder();
        var heardNothing = results.Count(r => r.HeardNothingDecoded);

        report.AppendLine(
            $"{results.Count} capture(s): {results.Sum(r => r.ValidFrames)} valid frame(s), "
            + $"{results.Sum(r => r.InvalidFrames)} CRC failure(s), "
            + $"{heardNothing} capture(s) heard a transmission but decoded nothing.");
        report.AppendLine();

        foreach (var result in results)
        {
            report.Append(Format(result));
            report.AppendLine();
        }

        return report.ToString();
    }
}
