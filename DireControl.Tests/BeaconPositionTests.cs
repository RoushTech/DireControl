using DireControl.Api.Services;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>APRS ddmm.mm / dddmm.mm position formatting for our own beacons.</summary>
[TestFixture]
public sealed class BeaconPositionTests
{
    [TestCase(45.99999, -85.0, "!4600.00N/08500.00W-")]
    [TestCase(35.30, -85.1333333, "!3518.00N/08508.00W-")]
    [TestCase(-33.5, 151.99999, "!3330.00S/15200.00E-")]
    [TestCase(0.0, 0.0, "!0000.00N/00000.00E-")]
    [TestCase(35.999917, -84.999917, "!3600.00N/08500.00W-", Description = "59.995' rounds up into the next degree")]
    public void BuildPositionInfo_RoundsBeforeSplittingDegreesAndMinutes(double lat, double lon, string expected)
    {
        Assert.That(BeaconService.BuildPositionInfo(lat, lon, "/-", null), Is.EqualTo(expected));
    }
}
