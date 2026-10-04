using DireControl.Modem.Audio;
using DireControl.Modem.Ax25;
using DireControl.Modem.Dsp;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// The capture-and-replay loop used to diagnose real decode failures.
///
/// Drop captured .wav files into <c>DireControl.Tests/Corpus/</c> (or point
/// <c>DIRECONTROL_CORPUS</c> somewhere else, such as the API's recordings
/// directory) and <see cref="Corpus_ReplayReport"/> replays each one through
/// the real demodulator and writes a diagnostic report.  The report is both
/// logged and written to <c>replay-report.txt</c> beside the corpus.
///
/// That test deliberately never fails: the corpus is a pile of known failures,
/// so failing on them would just mean a permanently red suite.  It reports.
/// <see cref="SynthesizedTransmission_SurvivesCaptureAndReplay"/> is the test
/// that actually asserts, guarding the harness itself.
/// </summary>
[TestFixture]
public sealed class AudioCorpusReplayTests
{
    private const int SampleRate = 48000;

    private static string CorpusDirectory =>
        Environment.GetEnvironmentVariable("DIRECONTROL_CORPUS")
        ?? Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "Corpus");

    [Test]
    public void SynthesizedTransmission_SurvivesCaptureAndReplay()
    {
        var frame = Ax25Encoder.EncodeUiFrame("N4WR-1", "!3518.00N/08508.00W-replay test", "WIDE1-1");
        var audio = new AfskModulator(SampleRate).GenerateFrame(frame);

        var path = Path.Combine(Path.GetTempPath(), $"direcontrol-replay-{Guid.NewGuid():N}.wav");
        try
        {
            WaveFile.Write(path, audio, SampleRate);
            var result = CorpusReplay.Replay(path);

            // The whole diagnostic loop in one assertion: a known transmission
            // survives being written to disk, read back, and demodulated.
            Assert.That(result.SampleRate, Is.EqualTo(SampleRate));
            Assert.That(result.ValidFrames, Is.EqualTo(1), CorpusReplay.Format(result));
            Assert.That(result.CarrierBursts, Is.GreaterThan(0));
            Assert.That(result.DecodedLines, Has.Count.EqualTo(1));
            Assert.That(result.DecodedLines[0], Does.Contain("N4WR-1"));
            Assert.That(result.ClippedPercent, Is.LessThan(1.0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void QuietAudio_IsDiagnosedAsLowLevel()
    {
        // Guards the diagnosis text that a human reads first in the report.
        var frame = Ax25Encoder.EncodeUiFrame("N4WR-1", "quiet", null);
        var audio = new AfskModulator(SampleRate).GenerateFrame(frame);
        for (var i = 0; i < audio.Length; i++)
            audio[i] *= 0.01f;

        var result = CorpusReplay.Replay("quiet.wav", new WaveFile.Audio(audio, SampleRate));

        Assert.That(CorpusReplay.Diagnose(result), Does.Contain("VERY LOW LEVEL"));
    }

    [Test]
    public void ClippedAudio_IsDiagnosedAsClipping()
    {
        var frame = Ax25Encoder.EncodeUiFrame("N4WR-1", "loud", null);
        var audio = new AfskModulator(SampleRate).GenerateFrame(frame);
        for (var i = 0; i < audio.Length; i++)
            audio[i] = Math.Clamp(audio[i] * 20f, -1f, 1f);

        var result = CorpusReplay.Replay("clipped.wav", new WaveFile.Audio(audio, SampleRate));

        Assert.That(CorpusReplay.Diagnose(result), Does.Contain("CLIPPING"));
    }

    [Test]
    public void Corpus_ReplayReport()
    {
        var directory = Path.GetFullPath(CorpusDirectory);
        if (!Directory.Exists(directory))
        {
            Assert.Ignore(
                $"No corpus directory at {directory}. Capture audio from the audio menu "
                + "(or let a missed decode auto-capture), drop the .wav files there, and re-run.");
        }

        var files = Directory.GetFiles(directory, "*.wav").Order().ToList();
        if (files.Count == 0)
            Assert.Ignore($"No .wav files in {directory} yet.");

        var results = new List<ReplayResult>();
        foreach (var file in files)
        {
            try
            {
                results.Add(CorpusReplay.Replay(file));
            }
            catch (Exception ex)
            {
                TestContext.Out.WriteLine($"{Path.GetFileName(file)}: could not replay — {ex.Message}");
            }
        }

        var report = CorpusReplay.FormatAll(results);
        TestContext.Out.WriteLine(report);

        // Written beside the corpus so the whole report can be read or shared
        // without digging through test output.
        try
        {
            File.WriteAllText(Path.Combine(directory, "replay-report.txt"), report);
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"Could not write replay-report.txt: {ex.Message}");
        }

        Assert.That(results, Is.Not.Empty, "Every capture in the corpus failed to replay.");
    }
}
