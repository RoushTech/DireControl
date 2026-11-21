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

    [TestCase("KM4ABC-9>APRS:>hi\r\nEVIL>APRS,TCPIP*:!spoof", "KM4ABC-9>APRS,qAR,N0CALL-10:>hi")]
    [TestCase("KM4ABC-9>APRS:>hi\nEVIL>APRS:x", "KM4ABC-9>APRS,qAR,N0CALL-10:>hi")]
    [TestCase("KM4ABC-9>APRS:>hi\rthere", "KM4ABC-9>APRS,qAR,N0CALL-10:>hi")]
    public void RfToIs_CrLf_TruncatesInfoField(string tnc2, string expected)
    {
        var line = IgateLogic.BuildRfToIsLine(tnc2, "N0CALL-10");

        Assert.That(line, Is.EqualTo(expected));
        Assert.That(line, Does.Not.Contain("\r").And.Not.Contain("\n"));
    }

    [TestCase("KM4ABC-9>APRS:\r\nEVIL>APRS:x", Description = "Empty after truncation")]
    [TestCase("KM4ABC-9>APRS:>hi\0there", Description = "Embedded NUL")]
    public void RfToIs_EmptyAfterCrLfOrNul_IsBlocked(string tnc2)
    {
        Assert.That(IgateLogic.BuildRfToIsLine(tnc2, "N0CALL-10"), Is.Null);
    }

    [TestCase("W1AW>APRS,TCPXX*,qAX,T2X::KM4ABC-9 :hello{42")]
    [TestCase("W1AW>APRS,NOGATE,qAR,K4ABC::KM4ABC-9 :hello{42")]
    [TestCase("W1AW>APRS,RFONLY,qAR,K4ABC::KM4ABC-9 :hello{42")]
    [TestCase("W1AW>APRS,TCPIP*,qAX,T2X::KM4ABC-9 :hello{42")]
    public void IsToRf_NoGatePaths_AreBlocked(string line)
    {
        Assert.That(IgateLogic.BuildIsToRfThirdPartyInfo(line, Gate), Is.Null);
    }

    [Test]
    public void IsToRfRateLimiter_EnforcesPerMinuteAndPerFiveMinuteBudgets()
    {
        var clock = new DireControl.Tests.Fakes.AutoAdvanceTimeProvider();
        var limiter = new IsToRfRateLimiter(clock);

        for (var i = 0; i < IsToRfRateLimiter.PerMinute; i++)
            Assert.That(limiter.TryAcquire(), Is.True, $"packet {i + 1} in the first minute");
        Assert.That(limiter.TryAcquire(), Is.False, "seventh packet within a minute");

        clock.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < IsToRfRateLimiter.PerFiveMinutes - IsToRfRateLimiter.PerMinute; i++)
            Assert.That(limiter.TryAcquire(), Is.True);
        Assert.That(limiter.TryAcquire(), Is.False, "eleventh packet within five minutes");

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.That(limiter.TryAcquire(), Is.True, "the first minute's packets have aged out");
    }

    [Test]
    public void RfToIs_PathWithQConstruct_IsBlocked()
    {
        Assert.That(IgateLogic.BuildRfToIsLine("KM4ABC-9>APRS,WIDE1-1,qAR,K4XYZ:>looped", Gate), Is.Null);
    }

    [Test]
    public void RfToIs_ThirdPartyFromRf_IsUnwrappedAndGated()
    {
        Assert.That(
            IgateLogic.BuildRfToIsLine("K4GW>APRS,WIDE2-1:}W1AW>APRS,K4DIGI*:>hello from afar", Gate),
            Is.EqualTo("W1AW>APRS,K4DIGI*,qAR,N4WR-2:>hello from afar"));
    }

    [Test]
    public void RfToIs_NestedThirdParty_IsBlocked()
    {
        Assert.That(IgateLogic.BuildRfToIsLine("K4GW>APRS:}W1AW>APRS:}K1X>APRS:>x", Gate), Is.Null);
    }
}
