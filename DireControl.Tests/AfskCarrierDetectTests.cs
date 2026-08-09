using DireControl.Modem;
using DireControl.Modem.Ax25;
using DireControl.Modem.Dsp;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Data-carrier detect: a real transmission (preamble of back-to-back flags)
/// must arm DCD, while silence and band noise must not — lone noise-decoded
/// 0x7E patterns previously held "Carrier: detected" on a dead band.
/// </summary>
[TestFixture]
public sealed class AfskCarrierDetectTests
{
    private const int SampleRate = 48000;

    [Test]
    public void RealTransmission_ArmsCarrierDetect()
    {
        var frame = Ax25Encoder.EncodeUiFrame("N4WR-1", "!3518.00N/08508.00W-DCD test", "WIDE1-1");
        var audio = new AfskModulator(SampleRate).GenerateFrame(frame);

        var receiver = AfskReceiver.CreateStandard(SampleRate);
        receiver.ProcessSamples(audio);

        Assert.That(receiver.CarrierDetected, Is.True,
            "a modulated frame's flag preamble should arm DCD");
    }

    [Test]
    public void Silence_DoesNotArmCarrierDetect()
    {
        var receiver = AfskReceiver.CreateStandard(SampleRate);
        receiver.ProcessSamples(new float[SampleRate * 2]);

        Assert.That(receiver.CarrierDetected, Is.False, "silence must not arm DCD");
    }

    [Test]
    public void NoiseOnly_DoesNotHoldCarrierDetect()
    {
        // Deterministic band noise — the exact case from the field report: RF dead,
        // audio floor only, yet the old single-flag DCD kept showing "detected".
        var random = new Random(1234);
        var noise = new float[SampleRate * 3];
        for (var i = 0; i < noise.Length; i++)
            noise[i] = (float)(random.NextDouble() * 2 - 1) * 0.05f;

        var receiver = AfskReceiver.CreateStandard(SampleRate);
        receiver.ProcessSamples(noise);

        Assert.That(receiver.CarrierDetected, Is.False,
            "band noise must not hold DCD armed");
    }
}
