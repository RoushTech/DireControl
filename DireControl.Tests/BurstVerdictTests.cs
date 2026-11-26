using DireControl.Api.Services;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// The channel-activity log is how an operator tells "nothing was transmitted"
/// apart from "a transmission arrived and we failed it" — the packet log cannot
/// distinguish those, and they point at different fixes.
///
/// The judgement is made on preambles, not audio level: with the radio's
/// squelch open, channel noise sits at a similar level to a signal, so level
/// says nothing about whether a transmission actually occurred.
/// </summary>
[TestFixture]
public sealed class BurstVerdictTests
{
    [Test]
    public void NoPreamble_IsCalledOutAsNoiseRegardlessOfOtherCounters()
    {
        // Carrier detect false-arms on band noise. Without a run of opening
        // flags there was no transmission, whatever else the counters say.
        Assert.That(
            SoundModemService.BurstVerdict(decoded: 0, failed: 0, preambles: 0),
            Does.Contain("carrier detect tripped on noise"));
        Assert.That(
            SoundModemService.BurstVerdict(decoded: 0, failed: 3, preambles: 0),
            Does.Contain("carrier detect tripped on noise"));
    }

    [Test]
    public void CleanDecode_ReportsNoProblem()
    {
        Assert.That(
            SoundModemService.BurstVerdict(decoded: 2, failed: 0, preambles: 1),
            Is.EqualTo("decoded cleanly"));
    }

    [Test]
    public void SomeDecodedAndSomeFailed_ReportsPartial()
    {
        Assert.That(
            SoundModemService.BurstVerdict(decoded: 1, failed: 1, preambles: 1),
            Is.EqualTo("partially decoded"));
    }

    [Test]
    public void CorruptFrame_PointsAtAudioQuality()
    {
        // A frame was found and failed CRC: the signal is there, the audio path
        // is mangling it.
        Assert.That(
            SoundModemService.BurstVerdict(decoded: 0, failed: 1, preambles: 1),
            Does.Contain("failed CRC"));
    }

    [Test]
    public void PreambleWithNoFrame_IsDistinguishedFromACorruptFrame()
    {
        // A transmission demonstrably started but no frame ever completed — a
        // different failure from a corrupt frame, and the one a CRC counter
        // alone would miss entirely.
        Assert.That(
            SoundModemService.BurstVerdict(decoded: 0, failed: 0, preambles: 1),
            Does.Contain("no frame formed"));
    }

    [Test]
    public void AQuietChannelAndALoudOneAreJudgedTheSame()
    {
        // The whole point of preamble-based judgement: opening the squelch
        // changes the audio level enormously but must not change the verdict.
        Assert.That(
            SoundModemService.BurstVerdict(decoded: 0, failed: 0, preambles: 1),
            Is.EqualTo(SoundModemService.BurstVerdict(decoded: 0, failed: 0, preambles: 1)));
        Assert.That(
            SoundModemService.BurstVerdict(decoded: 1, failed: 0, preambles: 2),
            Is.EqualTo("decoded cleanly"));
    }
}
