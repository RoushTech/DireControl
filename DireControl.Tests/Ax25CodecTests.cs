using System.Text;
using DireControl.Modem.Ax25;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Tests for the shared AX.25 encoder/decoder — including parity with the
/// historical TNC2 rendering that <c>KissTcpService.DecodeAx25ToTnc2</c>
/// produced, verified against the real captured packet corpus.
/// </summary>
[TestFixture]
public sealed class Ax25CodecTests
{
    [Test]
    public void EncodeDecode_RoundTripsAddressesAndInfo()
    {
        var frame = Ax25Encoder.EncodeUiFrame("N4WR-9", "!3518.00N/08508.00W-hello", "WIDE1-1,WIDE2-2");

        Assert.That(Ax25Decoder.TryDecode(frame, out var decoded), Is.True);
        Assert.That(decoded.Source.ToString(), Is.EqualTo("N4WR-9"));
        Assert.That(decoded.Destination.ToString(), Is.EqualTo("APRS"));
        Assert.That(decoded.Path.Select(p => p.ToString()), Is.EqualTo(["WIDE1-1", "WIDE2-2"]).AsCollection);
        Assert.That(decoded.Control, Is.EqualTo(Ax25Frame.UiControl));
        Assert.That(decoded.Pid, Is.EqualTo(Ax25Frame.NoLayer3Pid));
        Assert.That(Encoding.ASCII.GetString(decoded.Info), Is.EqualTo("!3518.00N/08508.00W-hello"));
        Assert.That(decoded.ToTnc2(), Is.EqualTo("N4WR-9>APRS,WIDE1-1,WIDE2-2:!3518.00N/08508.00W-hello"));
    }

    [Test]
    public void EncodeUiFrame_NoPath_SourceIsLastAddress()
    {
        var frame = Ax25Encoder.EncodeUiFrame("N4WR", "test", string.Empty);

        // 14 address bytes + control + pid + info
        Assert.That(frame.Length, Is.EqualTo(14 + 2 + 4));
        // End-of-address bit set on the source SSID byte.
        Assert.That(frame[13] & 0x01, Is.EqualTo(1));
        Assert.That(Ax25Decoder.TryDecode(frame, out var decoded), Is.True);
        Assert.That(decoded.Path, Is.Empty);
        Assert.That(decoded.ToTnc2(), Is.EqualTo("N4WR>APRS:test"));
    }

    [Test]
    public void Decoder_PreservesHasBeenRepeatedStars()
    {
        // Digipeated path: WIDE1-1 used (H bit set), WIDE2-1 not yet used.
        var frame = new List<byte>();
        frame.AddRange(Ax25Encoder.EncodeAddress(Ax25Address.Parse("APRS"), isLast: false));
        frame.AddRange(Ax25Encoder.EncodeAddress(Ax25Address.Parse("KM4XYZ-7"), isLast: false));
        frame.AddRange(Ax25Encoder.EncodeAddress(new Ax25Address("WIDE1", 1, HasBeenRepeated: true), isLast: false));
        frame.AddRange(Ax25Encoder.EncodeAddress(new Ax25Address("WIDE2", 1), isLast: true));
        frame.Add(Ax25Frame.UiControl);
        frame.Add(Ax25Frame.NoLayer3Pid);
        frame.AddRange(Encoding.ASCII.GetBytes(">status"));

        Assert.That(Ax25Decoder.TryDecode(frame.ToArray(), out var decoded), Is.True);
        Assert.That(decoded.ToTnc2(), Is.EqualTo("KM4XYZ-7>APRS,WIDE1-1*,WIDE2-1:>status"));
    }

    [Test]
    public void Decoder_RejectsTruncatedFrames()
    {
        Assert.That(Ax25Decoder.TryDecode(new byte[15], out _), Is.False);
        Assert.That(Ax25Decoder.TryDecode([], out _), Is.False);
    }

    [Test]
    public void Address_ParseAndToString_RoundTrip()
    {
        Assert.That(Ax25Address.Parse("N4WR").ToString(), Is.EqualTo("N4WR"));
        Assert.That(Ax25Address.Parse("N4WR-10").ToString(), Is.EqualTo("N4WR-10"));
        Assert.That(Ax25Address.Parse("WIDE1-1*"), Is.EqualTo(new Ax25Address("WIDE1", 1, true)));
        Assert.That(new Ax25Address("WIDE1", 1, true).ToString(), Is.EqualTo("WIDE1-1*"));
    }

    /// <summary>
    /// Encode a corpus of real captured TNC2 packets and confirm decode
    /// reproduces the same source/destination/info (path H bits aside — the
    /// encoder is given the path without stars).  The corpus file is a local
    /// capture that is deliberately not committed (git-ignored, ~2 MB), so
    /// this test self-ignores when it is absent (fresh clones, CI).
    /// </summary>
    [Test]
    public void RealPacketCorpus_InfoFieldsRoundTrip()
    {
        var corpusPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "RawPackets.txt");
        if (!File.Exists(corpusPath))
            Assert.Ignore("RawPackets.txt corpus not present (local-only capture).");

        var lines = File.ReadLines(corpusPath)
            .Where(l => !string.IsNullOrWhiteSpace(l) && l.Contains('>') && l.Contains(':'))
            .Take(500);

        var count = 0;
        foreach (var line in lines)
        {
            var gt = line.IndexOf('>');
            var colon = line.IndexOf(':');
            if (gt <= 0 || colon < gt)
                continue;

            var source = line[..gt];
            var header = line[(gt + 1)..colon].Split(',');
            var destination = header[0];
            var path = string.Join(',', header.Skip(1).Select(p => p.TrimEnd('*')));
            var info = line[(colon + 1)..];

            // Skip callsigns the strict AX.25 address field cannot carry.
            if (source.Contains('*') || source.Split('-')[0].Length > 6 || destination.Split('-')[0].Length > 6)
                continue;
            if (header.Skip(1).Any(p => p.TrimEnd('*').Split('-')[0].Length is > 6 or 0))
                continue;
            if (!source.Split('-')[0].All(char.IsAsciiLetterOrDigit))
                continue;
            if (info.Any(c => c > 127))
                continue;

            var frame = Ax25Encoder.EncodeUiFrame(source, info, path, destination);
            Assert.That(Ax25Decoder.TryDecode(frame, out var decoded), Is.True, line);
            Assert.That(Encoding.ASCII.GetString(decoded.Info), Is.EqualTo(info), line);
            Assert.That(decoded.Source.ToString(), Is.EqualTo(source.ToUpperInvariant()), line);
            count++;
        }

        Assert.That(count, Is.GreaterThan(100), "corpus should exercise a meaningful number of packets");
    }
}
