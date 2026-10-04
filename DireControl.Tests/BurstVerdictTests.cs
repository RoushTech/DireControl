using DireControl.Api.Services;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// The channel-activity log is how an operator tells "nothing was transmitted"
/// apart from "a transmission arrived and we failed it" — the packet log cannot
/// distinguish those, and they point at different fixes.
/// </summary>
[TestFixture]
public sealed class BurstVerdictTests
{
    private const float StrongPeak = 0.3f;
    private const float NoisePeak = 0.01f;

    [Test]
    public void WeakBurst_IsCalledOutAsProbablyNotRealRegardlessOfCounters()
    {
        // Carrier detect false-arms on band noise, so level is checked first —
        // otherwise a dead band reads as a stream of decode failures.
        Assert.That(
            SoundModemService.BurstVerdict(decoded: 0, failed: 0, NoisePeak),
            Does.Contain("probably not a real transmission"));
        Assert.That(
            SoundModemService.BurstVerdict(decoded: 0, failed: 3, NoisePeak),
            Does.Contain("probably not a real transmission"));
    }

    [Test]
    public void CleanDecode_ReportsNoProblem()
    {
        Assert.That(
            SoundModemService.BurstVerdict(decoded: 2, failed: 0, StrongPeak),
            Is.EqualTo("decoded cleanly"));
    }

    [Test]
    public void SomeDecodedAndSomeFailed_ReportsPartial()
    {
        Assert.That(
            SoundModemService.BurstVerdict(decoded: 1, failed: 1, StrongPeak),
            Is.EqualTo("partially decoded"));
    }

    [Test]
    public void CorruptFrame_PointsAtAudioQuality()
    {
        // A frame was found and failed CRC: the signal is there, the audio path
        // is mangling it.
        Assert.That(
            SoundModemService.BurstVerdict(decoded: 0, failed: 1, StrongPeak),
            Does.Contain("failed CRC"));
    }

    [Test]
    public void PreambleWithNoFrame_IsDistinguishedFromACorruptFrame()
    {
        // Flags armed carrier but no frame ever completed — a different failure
        // from a corrupt frame, and the one a CRC counter alone would miss.
        Assert.That(
            SoundModemService.BurstVerdict(decoded: 0, failed: 0, StrongPeak),
            Does.Contain("never found a complete frame"));
    }
}
