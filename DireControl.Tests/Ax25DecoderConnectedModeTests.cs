using System.Text;
using DireControl.Modem.Ax25;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Decoder/encoder tests for connected-mode (non-UI) AX.25 frames: S and U
/// frames without PID bytes, modulo-128 re-decoding, and C-bit handling.
/// </summary>
[TestFixture]
public sealed class Ax25DecoderConnectedModeTests
{
    private static List<byte> AddressBlock(
        string dest, string src, bool destCBit = true, bool srcCBit = false)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Ax25Encoder.EncodeAddress(Ax25Address.Parse(dest), isLast: false, topBit: destCBit));
        bytes.AddRange(Ax25Encoder.EncodeAddress(Ax25Address.Parse(src), isLast: true, topBit: srcCBit));
        return bytes;
    }

    [Test]
    public void Decode_BareRrFrame_FifteenBytes()
    {
        // RR response, N(R)=3, F=1 — 14 address bytes + 1 control byte, no PID.
        var frame = AddressBlock("N4WR-7", "KM4ACK-1", destCBit: false, srcCBit: true);
        frame.AddRange(Ax25ControlField.Build(Ax25FrameType.RR, extended: false, ns: 0, nr: 3, pollFinal: true));

        Assert.That(frame, Has.Count.EqualTo(15));
        Assert.That(Ax25Decoder.TryDecode(frame.ToArray(), out var decoded), Is.True,
            "a 15-byte S frame must decode (regression: old MinFrameLength=16 rejected these)");
        Assert.That(decoded.FrameType, Is.EqualTo(Ax25FrameType.RR));
        Assert.That(decoded.Nr, Is.EqualTo(3));
        Assert.That(decoded.Ns, Is.Null);
        Assert.That(decoded.PollFinal, Is.True);
        Assert.That(decoded.Pid, Is.Null);
        Assert.That(decoded.Info, Is.Empty);
        Assert.That(decoded.DestCommandBit, Is.False);
        Assert.That(decoded.SourceCommandBit, Is.True);
    }

    [TestCase(Ax25FrameType.SABM)]
    [TestCase(Ax25FrameType.SABME)]
    [TestCase(Ax25FrameType.DISC)]
    [TestCase(Ax25FrameType.DM)]
    [TestCase(Ax25FrameType.UA)]
    public void Decode_UFrames_NoPid(Ax25FrameType type)
    {
        var frame = AddressBlock("W3UWU", "KI4XYZ-2");
        frame.AddRange(Ax25ControlField.Build(type, extended: false, ns: 0, nr: 0, pollFinal: true));

        Assert.That(Ax25Decoder.TryDecode(frame.ToArray(), out var decoded), Is.True);
        Assert.That(decoded.FrameType, Is.EqualTo(type));
        Assert.That(decoded.Pid, Is.Null);
        Assert.That(decoded.PollFinal, Is.True);
        Assert.That(decoded.Info, Is.Empty);
    }

    [Test]
    public void Decode_UiFrame_KeepsPid()
    {
        var frame = Ax25Encoder.EncodeUiFrame("N4WR-9", "!test", "WIDE1-1");

        Assert.That(Ax25Decoder.TryDecode(frame, out var decoded), Is.True);
        Assert.That(decoded.FrameType, Is.EqualTo(Ax25FrameType.UI));
        Assert.That(decoded.Pid, Is.EqualTo(Ax25Frame.NoLayer3Pid));
        // EncodeUiFrame emits v2 command form: dest C=1, src C=0.
        Assert.That(decoded.DestCommandBit, Is.True);
        Assert.That(decoded.SourceCommandBit, Is.False);
    }

    [Test]
    public void Decode_IFrame_Mod8()
    {
        var frame = AddressBlock("W3UWU-1", "KI4XYZ");
        frame.AddRange(Ax25ControlField.Build(Ax25FrameType.I, extended: false, ns: 2, nr: 5, pollFinal: false));
        frame.Add(Ax25Frame.NoLayer3Pid);
        frame.AddRange(Encoding.ASCII.GetBytes("hello"));

        Assert.That(Ax25Decoder.TryDecode(frame.ToArray(), out var decoded), Is.True);
        Assert.That(decoded.FrameType, Is.EqualTo(Ax25FrameType.I));
        Assert.That(decoded.Ns, Is.EqualTo(2));
        Assert.That(decoded.Nr, Is.EqualTo(5));
        Assert.That(decoded.Pid, Is.EqualTo(Ax25Frame.NoLayer3Pid));
        Assert.That(Encoding.ASCII.GetString(decoded.Info), Is.EqualTo("hello"));
    }

    [Test]
    public void Decode_IFrame_TruncatedBeforePid_Rejected()
    {
        var frame = AddressBlock("W3UWU-1", "KI4XYZ");
        frame.AddRange(Ax25ControlField.Build(Ax25FrameType.I, extended: false, ns: 0, nr: 0, pollFinal: false));

        Assert.That(Ax25Decoder.TryDecode(frame.ToArray(), out _), Is.False,
            "an I frame with no PID byte is malformed");
    }

    [Test]
    public void Decode_Mod128IFrame_WithExtendedHint()
    {
        var frame = AddressBlock("W3UWU-1", "KI4XYZ");
        frame.AddRange(Ax25ControlField.Build(Ax25FrameType.I, extended: true, ns: 100, nr: 42, pollFinal: true));
        frame.Add(Ax25Frame.NoLayer3Pid);
        frame.AddRange(Encoding.ASCII.GetBytes("x"));
        var raw = frame.ToArray();

        // Session-owned re-decode with the mod-128 hint sees the real fields.
        Assert.That(Ax25Decoder.TryDecode(raw, out var extended, extendedControl: true), Is.True);
        Assert.That(extended.FrameType, Is.EqualTo(Ax25FrameType.I));
        Assert.That(extended.Ns, Is.EqualTo(100));
        Assert.That(extended.Nr, Is.EqualTo(42));
        Assert.That(extended.PollFinal, Is.True);
        Assert.That(extended.Control2, Is.Not.Null);

        // The default mod-8 decode still classifies it as an I frame (bit 0
        // of the first control byte is 0 in both moduli) — just with
        // modulo-8 field interpretation. It must not throw or reject.
        Assert.That(Ax25Decoder.TryDecode(raw, out var mod8), Is.True);
        Assert.That(mod8.FrameType, Is.EqualTo(Ax25FrameType.I));
        Assert.That(mod8.Control2, Is.Null);
    }

    [Test]
    public void Decode_FrmrFrame_InfoWithoutPid()
    {
        var frame = AddressBlock("W3UWU", "KI4XYZ", destCBit: false, srcCBit: true);
        frame.AddRange(Ax25ControlField.Build(Ax25FrameType.FRMR, extended: false, ns: 0, nr: 0, pollFinal: false));
        frame.AddRange([0x2F, 0x00, 0x10]); // 3-byte FRMR information field

        Assert.That(Ax25Decoder.TryDecode(frame.ToArray(), out var decoded), Is.True);
        Assert.That(decoded.FrameType, Is.EqualTo(Ax25FrameType.FRMR));
        Assert.That(decoded.Pid, Is.Null);
        Assert.That(decoded.Info, Has.Length.EqualTo(3));
    }

    [Test]
    public void Encode_RoundTrips_ConnectedModeFrames()
    {
        // SABM command with C bits, no PID, via Encode(Ax25Frame).
        var sabm = new Ax25Frame
        {
            Destination = Ax25Address.Parse("W3UWU-1"),
            Source = Ax25Address.Parse("KI4XYZ"),
            Control = Ax25ControlField.Build(Ax25FrameType.SABM, false, 0, 0, true)[0],
            Pid = null,
            DestCommandBit = true,
            SourceCommandBit = false,
        };
        var bytes = Ax25Encoder.Encode(sabm);

        Assert.That(bytes, Has.Length.EqualTo(15));
        Assert.That(Ax25Decoder.TryDecode(bytes, out var decoded), Is.True);
        Assert.That(decoded.FrameType, Is.EqualTo(Ax25FrameType.SABM));
        Assert.That(decoded.Pid, Is.Null);
        Assert.That(decoded.DestCommandBit, Is.True);
        Assert.That(decoded.SourceCommandBit, Is.False);
        Assert.That(decoded.PollFinal, Is.True);

        // Mod-128 I frame with Control2 survives an encode/decode cycle.
        var control = Ax25ControlField.Build(Ax25FrameType.I, extended: true, ns: 5, nr: 99, pollFinal: false);
        var iFrame = new Ax25Frame
        {
            Destination = Ax25Address.Parse("W3UWU-1"),
            Source = Ax25Address.Parse("KI4XYZ"),
            Control = control[0],
            Control2 = control[1],
            Pid = Ax25Frame.NoLayer3Pid,
            DestCommandBit = true,
            Info = Encoding.ASCII.GetBytes("data"),
        };
        var iBytes = Ax25Encoder.Encode(iFrame);

        Assert.That(Ax25Decoder.TryDecode(iBytes, out var iDecoded, extendedControl: true), Is.True);
        Assert.That(iDecoded.FrameType, Is.EqualTo(Ax25FrameType.I));
        Assert.That(iDecoded.Ns, Is.EqualTo(5));
        Assert.That(iDecoded.Nr, Is.EqualTo(99));
        Assert.That(iDecoded.Pid, Is.EqualTo(Ax25Frame.NoLayer3Pid));
        Assert.That(Encoding.ASCII.GetString(iDecoded.Info), Is.EqualTo("data"));
    }

    [Test]
    public void Decode_DigipeatedFrame_PathHBitsStillWork()
    {
        // C bits on dest/src and H bits on path entries coexist.
        var frame = new List<byte>();
        frame.AddRange(Ax25Encoder.EncodeAddress(Ax25Address.Parse("W3UWU-1"), isLast: false, topBit: true));
        frame.AddRange(Ax25Encoder.EncodeAddress(Ax25Address.Parse("KI4XYZ"), isLast: false, topBit: false));
        frame.AddRange(Ax25Encoder.EncodeAddress(new Ax25Address("WIDE1", 1, HasBeenRepeated: true), isLast: true));
        frame.AddRange(Ax25ControlField.Build(Ax25FrameType.SABM, false, 0, 0, true));

        Assert.That(Ax25Decoder.TryDecode(frame.ToArray(), out var decoded), Is.True);
        Assert.That(decoded.FrameType, Is.EqualTo(Ax25FrameType.SABM));
        Assert.That(decoded.DestCommandBit, Is.True);
        Assert.That(decoded.Path, Has.Count.EqualTo(1));
        Assert.That(decoded.Path[0].HasBeenRepeated, Is.True);
    }
}
