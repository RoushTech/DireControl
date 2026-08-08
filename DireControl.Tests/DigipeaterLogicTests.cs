using DireControl.Api.Services;
using DireControl.Modem.Ax25;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// WIDEn-N digipeater path-handling cases: decrement, callsign substitution,
/// insertion, trapping, fill-in mode, exhausted paths, and own-alias hops.
/// </summary>
[TestFixture]
public sealed class DigipeaterLogicTests
{
    private const string MyCall = "N4WR-2";

    private static Ax25Frame MakeFrame(string path)
    {
        var bytes = Ax25Encoder.EncodeUiFrame("KM4ABC-9", "!3518.00N/08508.00W-test", path);
        Assert.That(Ax25Decoder.TryDecode(bytes, out var frame), Is.True);
        return frame;
    }

    private static Ax25Frame MakeFrameWithUsedHops(params Ax25Address[] path)
    {
        return new Ax25Frame
        {
            Destination = new Ax25Address("APRS", 0),
            Source = new Ax25Address("KM4ABC", 9),
            Path = path,
            Info = "!test"u8.ToArray(),
        };
    }

    private static string? DigipeatedPath(Ax25Frame frame, int maxWideN = 2, bool fillInOnly = false)
    {
        var result = DigipeaterLogic.TryBuildDigipeat(frame, MyCall, maxWideN, fillInOnly);
        return result is null ? null : string.Join(",", result.Path.Select(p => p.ToString()));
    }

    [Test]
    public void Wide1_1_IsSubstitutedWithOurCall()
    {
        Assert.That(DigipeatedPath(MakeFrame("WIDE1-1")), Is.EqualTo("N4WR-2*"));
    }

    [Test]
    public void Wide2_2_DecrementsAndInsertsOurCall()
    {
        Assert.That(DigipeatedPath(MakeFrame("WIDE2-2")), Is.EqualTo("N4WR-2*,WIDE2-1"));
    }

    [Test]
    public void Wide2_1_LastHop_IsSubstituted()
    {
        Assert.That(DigipeatedPath(MakeFrame("WIDE2-1")), Is.EqualTo("N4WR-2*"));
    }

    [Test]
    public void ClassicTwoHopPath_ServesFirstHopOnly()
    {
        // WIDE1-1,WIDE2-1 → serve WIDE1-1 by substitution, leave WIDE2-1.
        Assert.That(DigipeatedPath(MakeFrame("WIDE1-1,WIDE2-1")), Is.EqualTo("N4WR-2*,WIDE2-1"));
    }

    [Test]
    public void UsedFirstHop_ServesNextAlias()
    {
        // A previous digi already handled WIDE1-1 (H bit set).
        var frame = MakeFrameWithUsedHops(
            new Ax25Address("K4DIG", 1, HasBeenRepeated: true),
            new Ax25Address("WIDE2", 2));

        Assert.That(DigipeatedPath(frame), Is.EqualTo("K4DIG-1*,N4WR-2*,WIDE2-1"));
    }

    [Test]
    public void ExhaustedPath_IsNotRepeated()
    {
        var frame = MakeFrameWithUsedHops(new Ax25Address("K4DIG", 1, HasBeenRepeated: true));
        Assert.That(DigipeatedPath(frame), Is.Null);
    }

    [Test]
    public void EmptyPath_IsNotRepeated()
    {
        Assert.That(DigipeatedPath(MakeFrame(string.Empty)), Is.Null);
    }

    [Test]
    public void AbusiveWide7_IsTrapped()
    {
        // n above the max is digipeated once with substitution — path dead.
        Assert.That(DigipeatedPath(MakeFrame("WIDE7-7"), maxWideN: 2), Is.EqualTo("N4WR-2*"));
    }

    [Test]
    public void Wide3_AllowedWhenMaxRaised()
    {
        Assert.That(DigipeatedPath(MakeFrame("WIDE3-3"), maxWideN: 3), Is.EqualTo("N4WR-2*,WIDE3-2"));
    }

    [Test]
    public void OurOwnCallsignAsHop_IsMarkedUsed()
    {
        Assert.That(DigipeatedPath(MakeFrame("N4WR-2,WIDE2-1")), Is.EqualTo("N4WR-2*,WIDE2-1"));
    }

    [Test]
    public void UnknownAlias_IsNotRepeated()
    {
        Assert.That(DigipeatedPath(MakeFrame("RELAY")), Is.Null);
        Assert.That(DigipeatedPath(MakeFrame("K9OTH-1")), Is.Null);
    }

    [Test]
    public void FillInMode_ServesOnlyWide1_1()
    {
        Assert.That(DigipeatedPath(MakeFrame("WIDE1-1,WIDE2-1"), fillInOnly: true), Is.EqualTo("N4WR-2*,WIDE2-1"));
        Assert.That(DigipeatedPath(MakeFrame("WIDE2-2"), fillInOnly: true), Is.Null);
        Assert.That(DigipeatedPath(MakeFrame("WIDE2-1"), fillInOnly: true), Is.Null);
    }

    [Test]
    public void NonUiFrame_IsNotRepeated()
    {
        var frame = new Ax25Frame
        {
            Destination = new Ax25Address("APRS", 0),
            Source = new Ax25Address("KM4ABC", 9),
            Path = [new Ax25Address("WIDE1", 1)],
            Control = 0x3F, // SABM, not UI
            Info = [],
        };
        Assert.That(DigipeaterLogic.TryBuildDigipeat(frame, MyCall, 2, false), Is.Null);
    }

    [Test]
    public void RewrittenFrame_ReencodesAndDecodesWithHBits()
    {
        var rewritten = DigipeaterLogic.TryBuildDigipeat(MakeFrame("WIDE2-2"), MyCall, 2, false)!;
        var bytes = Ax25Encoder.Encode(rewritten);

        Assert.That(Ax25Decoder.TryDecode(bytes, out var decoded), Is.True);
        Assert.That(decoded.ToTnc2(), Is.EqualTo("KM4ABC-9>APRS,N4WR-2*,WIDE2-1:!3518.00N/08508.00W-test"));
    }
}
