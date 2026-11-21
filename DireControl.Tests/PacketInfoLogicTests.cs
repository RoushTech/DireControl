using DireControl.Api.Services;
using NUnit.Framework;

namespace DireControl.Tests;

[TestFixture]
public sealed class PacketInfoLogicTests
{
    [Test]
    public void FormatStationCapabilities_RoundTripsKeysAndValues()
    {
        var caps = new Dictionary<string, string?> { ["IGATE"] = null, ["MSG_CNT"] = "10", ["LOC_CNT"] = "97" };

        Assert.That(PacketInfoLogic.FormatStationCapabilities(caps), Is.EqualTo("IGATE,MSG_CNT=10,LOC_CNT=97"));
    }

    [TestCase("IGATE", true)]
    [TestCase("igate", true)]
    [TestCase("IGATE,DISABLED", false)]
    [TestCase("MSG_CNT", false)]
    public void IsActiveIgateCapabilities(string keys, bool expected)
    {
        var caps = keys.Split(',').ToDictionary(k => k, _ => (string?)null);

        Assert.That(PacketInfoLogic.IsActiveIgateCapabilities(caps), Is.EqualTo(expected));
    }
}

[TestFixture]
public sealed class PlainTextBeaconTests
{
    [TestCase("WB4BSD>ID,WE4MB-3*,WIDE2*:WB4BSD/R WB4BSD-1/B\r", "WB4BSD/R WB4BSD-1/B")]
    [TestCase("KC4UG-1>APTT4,WA4TAL-2*,WIDE1*:Vernon , AL. 145.430 -.600 Pl. 110.9", "Vernon , AL. 145.430 -.600 Pl. 110.9")]
    [TestCase("NE4SC-12>BEACON,KN6RO-13*,WE4MB-3*,WIDE2*:NE4SC DIGI/WX Station in Aynor, SC WWW.NE4SC.ORG\r\n", "NE4SC DIGI/WX Station in Aynor, SC WWW.NE4SC.ORG")]
    [TestCase("W4ZHA-2>APTT4,TCPIP*,qAC,T2MCI:9.6V | 65F", "9.6V | 65F")]
    public void RecognisesPlainTextBeacons(string raw, string expectedText)
    {
        Assert.Multiple(() =>
        {
            Assert.That(PacketInfoLogic.TryGetPlainTextBeacon(raw, out var text), Is.True);
            Assert.That(text, Is.EqualTo(expectedText));
        });
    }

    [TestCase("KJ4NES-3>APTT4,K4UAN-3*,WIDE1*:/BG, KY, 35W 357ft HAAT, 8dBi Yagi")]          // has a DTI, just malformed
    [TestCase("WB4BYQ-3>APT311,W4LEE-1*:$GPRMC,091310,A,3242.4489,N,08527.2785,W,000,103")] // NMEA
    [TestCase("KO4KXN-1>APTT4,N4XWC*,WIDE2*:")]                                              // empty
    [TestCase("KM4KMO-10>APK102,WE4MB-3*,WIDE1*:\r")]                                        // whitespace only
    [TestCase("\u000fd]yr-6>Z~PCIr-1,%W=nd&-6*:Ft?")]                                        // corrupt frame
    [TestCase("4P\u0000\u000f-1>APRS:4P\u0000\u000f4T\u0000")]                               // control bytes
    [TestCase("no header here")]
    public void RejectsNonBeacons(string raw)
    {
        Assert.That(PacketInfoLogic.TryGetPlainTextBeacon(raw, out _), Is.False);
    }
}
