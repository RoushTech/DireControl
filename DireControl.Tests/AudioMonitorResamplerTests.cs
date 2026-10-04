using DireControl.Modem.Audio;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// The monitor resampler feeds the browser "listen to the radio" stream.  It
/// must decimate 48 kHz down to 8 kHz without aliasing (an unfiltered decimate
/// would fold 10 kHz hiss down onto the 2 kHz voice band) and must clamp rather
/// than wrap when the audio path is overdriven — wrapping would turn clipping
/// into loud noise and hide the very problem an operator is listening for.
/// </summary>
[TestFixture]
public sealed class AudioMonitorResamplerTests
{
    private const int SampleRate = 48000;

    private static List<byte[]> Run(AudioMonitorResampler resampler, float[] input)
    {
        var frames = new List<byte[]>();
        resampler.Process(input, frames.Add);
        return frames;
    }

    private static float[] Tone(float frequencyHz, float amplitude, int samples)
    {
        var audio = new float[samples];
        for (var i = 0; i < samples; i++)
            audio[i] = amplitude * MathF.Sin(2 * MathF.PI * frequencyHz * i / SampleRate);
        return audio;
    }

    private static float Rms(IEnumerable<byte[]> frames)
    {
        var samples = frames.SelectMany(Unpack).ToList();
        if (samples.Count == 0)
            return 0f;
        return MathF.Sqrt(samples.Sum(s => s * s) / samples.Count);
    }

    private static IEnumerable<float> Unpack(byte[] frame)
    {
        for (var i = 0; i < frame.Length; i += 2)
            yield return (short)(frame[i] | (frame[i + 1] << 8)) / (float)short.MaxValue;
    }

    [Test]
    public void OneSecondOfAudio_ProducesWholeFramesAtOutputRate()
    {
        var frames = Run(new AudioMonitorResampler(SampleRate), new float[SampleRate]);

        // 48 000 in at 48 kHz is one second, so 8 000 output samples, emitted as
        // whole 512-sample frames with the remainder held for the next call.
        Assert.That(frames, Has.Count.EqualTo(AudioMonitorResampler.OutputSampleRate / AudioMonitorResampler.FrameSamples));
        Assert.That(frames, Has.All.Length.EqualTo(AudioMonitorResampler.FrameBytes));
    }

    [Test]
    public void PartialFrame_IsHeldAcrossCalls()
    {
        var resampler = new AudioMonitorResampler(SampleRate);

        // One frame's worth of output needs 512 * 6 input samples; one short of
        // that must emit nothing, and the next sample completes the frame.
        var justShort = Run(resampler, new float[AudioMonitorResampler.FrameSamples * 6 - 6]);
        Assert.That(justShort, Is.Empty);

        var completed = Run(resampler, new float[6]);
        Assert.That(completed, Has.Count.EqualTo(1));
    }

    [Test]
    public void VoiceBandTone_PassesThroughAtFullAmplitude()
    {
        var frames = Run(new AudioMonitorResampler(SampleRate), Tone(1000f, 0.5f, SampleRate));

        // A 1 kHz tone is well inside the 3.4 kHz passband: RMS of a 0.5
        // amplitude sine is 0.3536, allowing for filter settling at the start.
        Assert.That(Rms(frames), Is.EqualTo(0.3536f).Within(0.02f));
    }

    [Test]
    public void AfskSpaceTone_SurvivesDecimation()
    {
        var frames = Run(new AudioMonitorResampler(SampleRate), Tone(2200f, 0.5f, SampleRate));

        // 2200 Hz is the AFSK space tone and the highest thing we care about
        // hearing; it must stay under the 4 kHz Nyquist intact.
        Assert.That(Rms(frames), Is.EqualTo(0.3536f).Within(0.03f));
    }

    [Test]
    public void OutOfBandTone_IsRejectedRatherThanAliased()
    {
        var frames = Run(new AudioMonitorResampler(SampleRate), Tone(10000f, 0.5f, SampleRate));

        // Without the anti-alias lowpass, 10 kHz decimated by 6 would fold down
        // to an audible 2 kHz whistle at full amplitude.
        Assert.That(Rms(frames), Is.LessThan(0.01f));
    }

    [Test]
    public void OverdrivenAudio_ClampsInsteadOfWrapping()
    {
        var input = new float[AudioMonitorResampler.FrameSamples * 6];
        Array.Fill(input, 4f);

        var frames = Run(new AudioMonitorResampler(SampleRate), input);
        var samples = frames.SelectMany(Unpack).ToList();

        // Nothing may exceed full scale: a 4.0 input cast without clamping would
        // overflow the 16-bit range and wrap to loud noise, masking the clipping
        // an operator is listening for. The filter's step response rings for its
        // first few taps, so only the settled audio is pinned at full scale.
        Assert.That(samples, Has.All.InRange(-1f, 1f));
        Assert.That(samples.Skip(16), Has.All.EqualTo(1f).Within(0.001f));
    }

    [Test]
    public void Gain_ScalesTheInputBeforeConversion()
    {
        var resampler = new AudioMonitorResampler(SampleRate);
        var frames = new List<byte[]>();

        // Transmit audio is monitored at a fixed level rather than its own drive
        // level, so the gain path has to actually attenuate.
        resampler.Process(Tone(1000f, 1.0f, SampleRate), frames.Add, 0.2f);

        Assert.That(Rms(frames), Is.EqualTo(0.7071f * 0.2f).Within(0.01f));
    }

    [Test]
    public void Reset_DiscardsPartialFrame()
    {
        var resampler = new AudioMonitorResampler(SampleRate);
        Run(resampler, new float[AudioMonitorResampler.FrameSamples * 6 - 6]);

        resampler.Reset();

        // The held partial frame is gone, so six more samples cannot complete one.
        Assert.That(Run(resampler, new float[6]), Is.Empty);
    }

    [Test]
    public void NonMultipleInputRate_IsRejected()
    {
        // Decimation is integer-only; a rate that does not divide cleanly would
        // silently detune the output rather than fail.
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioMonitorResampler(44100));
    }
}
