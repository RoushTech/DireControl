using DireControl.Modem.Ax25;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Tests for AX.25 control-field parsing and building across modulo-8 and
/// modulo-128 (extended) operation.
/// </summary>
[TestFixture]
public sealed class Ax25ControlFieldTests
{
    [TestCase(0x2F, Ax25FrameType.SABM)]
    [TestCase(0x6F, Ax25FrameType.SABME)]
    [TestCase(0x43, Ax25FrameType.DISC)]
    [TestCase(0x0F, Ax25FrameType.DM)]
    [TestCase(0x63, Ax25FrameType.UA)]
    [TestCase(0x87, Ax25FrameType.FRMR)]
    [TestCase(0x03, Ax25FrameType.UI)]
    [TestCase(0xAF, Ax25FrameType.XID)]
    [TestCase(0xE3, Ax25FrameType.TEST)]
    public void Parse_UFrameOpcodes(byte opcode, Ax25FrameType expected)
    {
        // Without and with the P/F bit — both must classify identically.
        foreach (var pf in new[] { false, true })
        {
            var c0 = (byte)(opcode | (pf ? 0x10 : 0x00));
            var type = Ax25ControlField.Parse([c0], extended: false,
                out _, out _, out var pollFinal, out var consumed);

            Assert.That(type, Is.EqualTo(expected));
            Assert.That(pollFinal, Is.EqualTo(pf));
            Assert.That(consumed, Is.EqualTo(1));
        }
    }

    [Test]
    public void Parse_UFrame_IgnoresExtendedHint()
    {
        // U frames are always one control byte, even on a mod-128 link.
        // 0x3F = SABM with P=1.
        var type = Ax25ControlField.Parse([0x3F, 0xFF], extended: true,
            out _, out _, out var pf, out var consumed);

        Assert.That(type, Is.EqualTo(Ax25FrameType.SABM));
        Assert.That(pf, Is.True);
        Assert.That(consumed, Is.EqualTo(1));
    }

    [Test]
    public void Parse_UnknownUOpcode_ReturnsUnknown()
    {
        var type = Ax25ControlField.Parse([0xFF], extended: false, out _, out _, out _, out var consumed);

        Assert.That(type, Is.EqualTo(Ax25FrameType.Unknown));
        Assert.That(consumed, Is.EqualTo(1));
    }

    [TestCase(Ax25FrameType.RR)]
    [TestCase(Ax25FrameType.RNR)]
    [TestCase(Ax25FrameType.REJ)]
    [TestCase(Ax25FrameType.SREJ)]
    public void SFrames_RoundTrip_Mod8(Ax25FrameType type)
    {
        for (var nr = 0; nr < 8; nr++)
        {
            foreach (var pf in new[] { false, true })
            {
                var bytes = Ax25ControlField.Build(type, extended: false, ns: 0, nr: nr, pollFinal: pf);
                Assert.That(bytes, Has.Length.EqualTo(1));

                var parsed = Ax25ControlField.Parse(bytes, extended: false,
                    out _, out var parsedNr, out var parsedPf, out var consumed);

                Assert.That(parsed, Is.EqualTo(type));
                Assert.That(parsedNr, Is.EqualTo(nr));
                Assert.That(parsedPf, Is.EqualTo(pf));
                Assert.That(consumed, Is.EqualTo(1));
            }
        }
    }

    [TestCase(Ax25FrameType.RR)]
    [TestCase(Ax25FrameType.RNR)]
    [TestCase(Ax25FrameType.REJ)]
    public void SFrames_RoundTrip_Mod128(Ax25FrameType type)
    {
        foreach (var nr in new[] { 0, 1, 63, 127 })
        {
            var bytes = Ax25ControlField.Build(type, extended: true, ns: 0, nr: nr, pollFinal: true);
            Assert.That(bytes, Has.Length.EqualTo(2));

            var parsed = Ax25ControlField.Parse(bytes, extended: true,
                out _, out var parsedNr, out var parsedPf, out var consumed);

            Assert.That(parsed, Is.EqualTo(type));
            Assert.That(parsedNr, Is.EqualTo(nr));
            Assert.That(parsedPf, Is.True);
            Assert.That(consumed, Is.EqualTo(2));
        }
    }

    [Test]
    public void IFrames_RoundTrip_Mod8()
    {
        for (var ns = 0; ns < 8; ns++)
        {
            for (var nr = 0; nr < 8; nr++)
            {
                var bytes = Ax25ControlField.Build(Ax25FrameType.I, extended: false, ns, nr, pollFinal: false);
                Assert.That(bytes, Has.Length.EqualTo(1));
                Assert.That(bytes[0] & 0x01, Is.Zero, "I frame control bit 0 must be 0");

                var parsed = Ax25ControlField.Parse(bytes, extended: false,
                    out var parsedNs, out var parsedNr, out var pf, out _);

                Assert.That(parsed, Is.EqualTo(Ax25FrameType.I));
                Assert.That(parsedNs, Is.EqualTo(ns));
                Assert.That(parsedNr, Is.EqualTo(nr));
                Assert.That(pf, Is.False);
            }
        }
    }

    [Test]
    public void IFrames_RoundTrip_Mod128()
    {
        foreach (var (ns, nr) in new[] { (0, 0), (1, 127), (127, 1), (64, 64) })
        {
            var bytes = Ax25ControlField.Build(Ax25FrameType.I, extended: true, ns, nr, pollFinal: true);
            Assert.That(bytes, Has.Length.EqualTo(2));

            var parsed = Ax25ControlField.Parse(bytes, extended: true,
                out var parsedNs, out var parsedNr, out var pf, out var consumed);

            Assert.That(parsed, Is.EqualTo(Ax25FrameType.I));
            Assert.That(parsedNs, Is.EqualTo(ns));
            Assert.That(parsedNr, Is.EqualTo(nr));
            Assert.That(pf, Is.True);
            Assert.That(consumed, Is.EqualTo(2));
        }
    }

    [Test]
    public void Parse_ExtendedIFrame_TruncatedSecondByte_ReturnsUnknown()
    {
        var type = Ax25ControlField.Parse([0x00], extended: true, out _, out _, out _, out var consumed);

        Assert.That(type, Is.EqualTo(Ax25FrameType.Unknown));
        Assert.That(consumed, Is.Zero);
    }

    [Test]
    public void HasPid_TrueOnlyForIAndUi()
    {
        Assert.That(Ax25ControlField.HasPid(Ax25FrameType.I), Is.True);
        Assert.That(Ax25ControlField.HasPid(Ax25FrameType.UI), Is.True);

        foreach (var type in new[]
        {
            Ax25FrameType.RR, Ax25FrameType.RNR, Ax25FrameType.REJ, Ax25FrameType.SREJ,
            Ax25FrameType.SABM, Ax25FrameType.SABME, Ax25FrameType.DISC, Ax25FrameType.DM,
            Ax25FrameType.UA, Ax25FrameType.FRMR, Ax25FrameType.XID, Ax25FrameType.TEST,
            Ax25FrameType.Unknown,
        })
        {
            Assert.That(Ax25ControlField.HasPid(type), Is.False, type.ToString());
        }
    }

    [Test]
    public void Build_KnownEncodings()
    {
        // Spot-check well-known control byte values.
        Assert.That(Ax25ControlField.Build(Ax25FrameType.SABM, false, 0, 0, true), Is.EqualTo(new byte[] { 0x3F }));
        Assert.That(Ax25ControlField.Build(Ax25FrameType.UA, false, 0, 0, true), Is.EqualTo(new byte[] { 0x73 }));
        Assert.That(Ax25ControlField.Build(Ax25FrameType.UI, false, 0, 0, false), Is.EqualTo(new byte[] { 0x03 }));
        // RR with N(R)=3, P=0 in mod-8: 011 0 00 01 = 0x61.
        Assert.That(Ax25ControlField.Build(Ax25FrameType.RR, false, 0, 3, false), Is.EqualTo(new byte[] { 0x61 }));
        // I frame N(S)=2, N(R)=5, P=1 in mod-8: 101 1 010 0 = 0xB4.
        Assert.That(Ax25ControlField.Build(Ax25FrameType.I, false, 2, 5, true), Is.EqualTo(new byte[] { 0xB4 }));
    }
}
