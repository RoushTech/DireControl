using DireControl.Api.Services;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>iGate gating rules for RF→IS lines and IS→RF third-party wrapping.</summary>
[TestFixture]
public sealed class IgateLogicTests
{
    private const string Gate = "N4WR-2";

    // ── RF→IS ────────────────────────────────────────────────────────────────

    [Test]
    public void RfToIs_AppendsQarConstruct()
    {
        Assert.That(
            IgateLogic.BuildRfToIsLine("KM4ABC-9>APRS,WIDE1-1*:!3518.00N/08508.00W-hi", Gate),
            Is.EqualTo("KM4ABC-9>APRS,WIDE1-1*,qAR,N4WR-2:!3518.00N/08508.00W-hi"));
    }

    [Test]
    public void RfToIs_DirectPacket_Gates()
    {
        Assert.That(
            IgateLogic.BuildRfToIsLine("KM4ABC-9>APRS:>status", Gate),
            Is.EqualTo("KM4ABC-9>APRS,qAR,N4WR-2:>status"));
    }

    [TestCase("KM4ABC-9>APRS,NOGATE:>x")]
    [TestCase("KM4ABC-9>APRS,RFONLY:>x")]
    [TestCase("KM4ABC-9>APRS,WIDE1-1,NOGATE:>x")]
    [TestCase("KM4ABC-9>APRS,TCPIP*:>x")]
    [TestCase("KM4ABC-9>APRS,TCPXX:>x")]
    public void RfToIs_NoGatePaths_AreBlocked(string tnc2)
    {
        Assert.That(IgateLogic.BuildRfToIsLine(tnc2, Gate), Is.Null);
    }

    [Test]
    public void RfToIs_ThirdPartyAndQueries_AreBlocked()
    {
        Assert.That(IgateLogic.BuildRfToIsLine("KM4ABC-9>APRS:}A>B,TCPIP,C*::D        :hi", Gate), Is.Null);
        Assert.That(IgateLogic.BuildRfToIsLine("KM4ABC-9>APRS:?APRS?", Gate), Is.Null);
    }

    [Test]
    public void RfToIs_MalformedLines_AreBlocked()
    {
        Assert.That(IgateLogic.BuildRfToIsLine("garbage", Gate), Is.Null);
        Assert.That(IgateLogic.BuildRfToIsLine("A>B", Gate), Is.Null);
        Assert.That(IgateLogic.BuildRfToIsLine(":info>only", Gate), Is.Null);
    }

    // ── IS→RF ────────────────────────────────────────────────────────────────

    [Test]
    public void IsToRf_WrapsMessageInThirdPartyFormat()
    {
        var line = "W1AW>APRS,TCPIP*,qAC,T2SERVER::KM4ABC-9 :hello there{42";
        Assert.That(
            IgateLogic.BuildIsToRfThirdPartyInfo(line, Gate),
            Is.EqualTo("}W1AW>APRS,TCPIP,N4WR-2*::KM4ABC-9 :hello there{42"));
    }

    [Test]
    public void IsToRf_NonMessages_AreBlocked()
    {
        Assert.That(IgateLogic.BuildIsToRfThirdPartyInfo("W1AW>APRS,qAC,T2X:!3518.00N/08508.00W-", Gate), Is.Null);
        Assert.That(IgateLogic.BuildIsToRfThirdPartyInfo("W1AW>APRS,qAC,T2X:>status", Gate), Is.Null);
    }

    [Test]
    public void ExtractMessageAddressee_ParsesAndTrims()
    {
        Assert.That(IgateLogic.ExtractMessageAddressee(":KM4ABC-9 :hi"), Is.EqualTo("KM4ABC-9"));
        Assert.That(IgateLogic.ExtractMessageAddressee(":W1AW     :ack42"), Is.EqualTo("W1AW"));
        Assert.That(IgateLogic.ExtractMessageAddressee(">status"), Is.Null);
        Assert.That(IgateLogic.ExtractMessageAddressee(":short:x"), Is.Null);
    }
}
