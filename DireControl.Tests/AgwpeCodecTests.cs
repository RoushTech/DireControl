using System.Text;
using DireControl.Modem.Agwpe;
using DireControl.Modem.Ax25;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>AGWPE frame codec and monitor-formatter tests.</summary>
[TestFixture]
public sealed class AgwpeCodecTests
{
    [Test]
    public void Encode_HeaderLayout()
    {
        var frame = AgwpeFrame.Create('C', port: 2, callFrom: "W3UWU-1", callTo: "KB4BBS-7",
            data: Encoding.ASCII.GetBytes("hello"));

        var bytes = AgwpeCodec.Encode(frame);

        Assert.That(bytes, Has.Length.EqualTo(36 + 5));
        Assert.That(bytes[0], Is.EqualTo(2), "port at offset 0");
        Assert.That(bytes[4], Is.EqualTo((byte)'C'), "kind at offset 4");
        Assert.That(Encoding.ASCII.GetString(bytes[8..18]).TrimEnd('\0'), Is.EqualTo("W3UWU-1"));
        Assert.That(Encoding.ASCII.GetString(bytes[18..28]).TrimEnd('\0'), Is.EqualTo("KB4BBS-7"));
        Assert.That(BitConverter.ToUInt32(bytes, 28), Is.EqualTo(5), "LE length at offset 28");
        Assert.That(Encoding.ASCII.GetString(bytes[36..]), Is.EqualTo("hello"));
    }

    [Test]
    public void RoundTrip_PreservesEverything()
    {
        var original = AgwpeFrame.Create('D', port: 1, callFrom: "KI4XYZ", callTo: "W3UWU-1",
            data: [0x00, 0xFF, 0x0D, 0x41], pid: 0xF0);

        var buffer = new List<byte>(AgwpeCodec.Encode(original));
        Assert.That(AgwpeCodec.TryDecode(buffer, out var decoded), Is.True);

        Assert.That(decoded.Kind, Is.EqualTo('D'));
        Assert.That(decoded.Port, Is.EqualTo(1));
        Assert.That(decoded.CallFrom, Is.EqualTo("KI4XYZ"));
        Assert.That(decoded.CallTo, Is.EqualTo("W3UWU-1"));
        Assert.That(decoded.Pid, Is.EqualTo(0xF0));
        Assert.That(decoded.Data, Is.EqualTo(new byte[] { 0x00, 0xFF, 0x0D, 0x41 }).AsCollection);
        Assert.That(buffer, Is.Empty, "consumed bytes removed");
    }

    [Test]
    public void TryDecode_IncrementalAcrossSplitReads()
    {
        var frame1 = AgwpeCodec.Encode(AgwpeFrame.Create('M'));
        var frame2 = AgwpeCodec.Encode(AgwpeFrame.Create('D', callFrom: "A", callTo: "B",
            data: Encoding.ASCII.GetBytes("payload")));
        var all = frame1.Concat(frame2).ToArray();

        var buffer = new List<byte>();
        var decoded = new List<AgwpeFrame>();

        // Feed in awkward 7-byte slices, decoding whenever possible.
        for (var offset = 0; offset < all.Length; offset += 7)
        {
            buffer.AddRange(all.Skip(offset).Take(7));
            while (AgwpeCodec.TryDecode(buffer, out var frame))
                decoded.Add(frame);
        }

        Assert.That(decoded, Has.Count.EqualTo(2));
        Assert.That(decoded[0].Kind, Is.EqualTo('M'));
        Assert.That(decoded[1].Kind, Is.EqualTo('D'));
        Assert.That(Encoding.ASCII.GetString(decoded[1].Data), Is.EqualTo("payload"));
    }

    [Test]
    public void TryDecode_PartialHeader_WaitsForMore()
    {
        var buffer = new List<byte>(AgwpeCodec.Encode(AgwpeFrame.Create('R')).Take(20));
        Assert.That(AgwpeCodec.TryDecode(buffer, out _), Is.False);
        Assert.That(buffer, Has.Count.EqualTo(20), "nothing consumed while incomplete");
    }

    [Test]
    public void TryDecode_InsaneLength_Throws()
    {
        var bytes = AgwpeCodec.Encode(AgwpeFrame.Create('D'));
        bytes[28] = 0xFF;
        bytes[29] = 0xFF;
        bytes[30] = 0xFF;
        bytes[31] = 0x7F;

        Assert.Throws<InvalidDataException>(() => AgwpeCodec.TryDecode([.. bytes], out _));
    }

    [Test]
    public void MonitorFormatter_UiFrame_GoldenString()
    {
        var raw = Ax25Encoder.EncodeUiFrame("KI4ABC", "!3518.00N/08508.00Wv", "WIDE1-1", "APRS");
        Assert.That(Ax25Decoder.TryDecode(raw, out var frame), Is.True);

        var payload = AgwpeMonitorFormatter.Format(frame, portDisplayNumber: 1,
            new DateTime(2026, 8, 10, 21, 14, 7));
        var text = Encoding.ASCII.GetString(payload);

        Assert.That(text, Is.EqualTo(
            " 1:Fm KI4ABC To APRS Via WIDE1-1 <UI pid=F0 Len=20 >[21:14:07]\r!3518.00N/08508.00Wv\r"));
    }

    [Test]
    public void MonitorFormatter_SupervisoryFrame()
    {
        var control = Ax25ControlField.Build(Ax25FrameType.RR, extended: false, ns: 0, nr: 3, pollFinal: true);
        var frame = new Ax25Frame
        {
            Destination = Ax25Address.Parse("W3UWU-1"),
            Source = Ax25Address.Parse("KB4BBS-7"),
            Control = control[0],
            Pid = null,
        };

        var text = Encoding.ASCII.GetString(
            AgwpeMonitorFormatter.Format(frame, 2, new DateTime(2026, 8, 10, 12, 0, 0)));

        Assert.That(text, Is.EqualTo(" 2:Fm KB4BBS-7 To W3UWU-1 <RR R3 >[12:00:00]\r"));
        Assert.That(AgwpeMonitorFormatter.MonitorKind(frame.FrameType), Is.EqualTo('S'));
    }

    [Test]
    public void MonitorKind_Classification()
    {
        Assert.That(AgwpeMonitorFormatter.MonitorKind(Ax25FrameType.UI), Is.EqualTo('U'));
        Assert.That(AgwpeMonitorFormatter.MonitorKind(Ax25FrameType.I), Is.EqualTo('I'));
        Assert.That(AgwpeMonitorFormatter.MonitorKind(Ax25FrameType.SABM), Is.EqualTo('S'));
        Assert.That(AgwpeMonitorFormatter.MonitorKind(Ax25FrameType.RR), Is.EqualTo('S'));
    }
}
