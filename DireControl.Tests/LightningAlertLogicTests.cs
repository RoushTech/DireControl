using System;
using DireControl.Api.Services.Weather;
using NUnit.Framework;

namespace DireControl.Tests;

[TestFixture]
public class LightningAlertLogicTests
{
    private static LightningStrike At(double lat, double lon)
        => new(DateTime.UtcNow, lat, lon);

    [Test]
    public void HaversineKm_OneDegreeOfLatitude_IsAbout111Km()
    {
        var km = LightningAlertLogic.HaversineKm(35, -85, 36, -85);
        Assert.That(km, Is.EqualTo(111.19).Within(0.5));
    }

    [Test]
    public void HaversineKm_SamePoint_IsZero()
    {
        Assert.That(LightningAlertLogic.HaversineKm(35, -85, 35, -85), Is.EqualTo(0));
    }

    [TestCase(35, -85, 36, -85, 0)]     // due north
    [TestCase(35, -85, 35, -84, 90)]    // due east
    [TestCase(35, -85, 34, -85, 180)]   // due south
    [TestCase(35, -85, 35, -86, 270)]   // due west
    public void BearingDegrees_CardinalDirections(
        double lat1, double lon1, double lat2, double lon2, double expected)
    {
        var bearing = LightningAlertLogic.BearingDegrees(lat1, lon1, lat2, lon2);
        Assert.That(bearing, Is.EqualTo(expected).Within(1.0));
    }

    [Test]
    public void BoundingBox_ContainsPointsWithinRadius()
    {
        var (minLat, maxLat, minLon, maxLon) = LightningAlertLogic.BoundingBox(35, -85, 50);

        // A strike 40 km due north/east must fall inside the box.
        Assert.That(minLat, Is.LessThan(35 + 40.0 / 111.32));
        Assert.That(maxLat, Is.GreaterThan(35 + 40.0 / 111.32));
        Assert.That(minLon, Is.LessThan(-85));
        Assert.That(maxLon, Is.GreaterThan(-85 + 40.0 / (111.32 * Math.Cos(35 * Math.PI / 180))));
    }

    [Test]
    public void BoundingBox_ClampsLatitudeAtPoles()
    {
        var (minLat, maxLat, _, _) = LightningAlertLogic.BoundingBox(89.9, 0, 100);
        Assert.That(maxLat, Is.EqualTo(90));
        Assert.That(minLat, Is.GreaterThan(88));
    }

    [Test]
    public void BoundingBox_NearPole_CoversAllLongitudes()
    {
        var (_, _, minLon, maxLon) = LightningAlertLogic.BoundingBox(89.95, 0, 100);
        Assert.That(maxLon - minLon, Is.GreaterThanOrEqualTo(360));
    }

    [Test]
    public void FindClosest_ReturnsNearestStrikeWithinRadius()
    {
        var strikes = new[]
        {
            At(36.0, -85),   // ~111 km away — outside a 50 km radius
            At(35.2, -85),   // ~22 km away
            At(35.1, -85),   // ~11 km away — closest
        };

        var closest = LightningAlertLogic.FindClosest(strikes, 35, -85, 50);

        Assert.That(closest, Is.Not.Null);
        Assert.That(closest!.Value.Strike.Latitude, Is.EqualTo(35.1));
        Assert.That(closest.Value.DistanceKm, Is.EqualTo(11.1).Within(0.2));
    }

    [Test]
    public void FindClosest_NoStrikeWithinRadius_ReturnsNull()
    {
        var strikes = new[] { At(36.0, -85) }; // ~111 km away

        Assert.That(LightningAlertLogic.FindClosest(strikes, 35, -85, 50), Is.Null);
    }

    [Test]
    public void FindClosest_EmptyInput_ReturnsNull()
    {
        Assert.That(LightningAlertLogic.FindClosest([], 35, -85, 50), Is.Null);
    }

    [Test]
    public void CooldownElapsed_FirstAlert_AlwaysFires()
    {
        Assert.That(LightningAlertLogic.CooldownElapsed(DateTime.UtcNow, null, TimeSpan.FromMinutes(5)), Is.True);
    }

    [Test]
    public void CooldownElapsed_WithinCooldown_Suppresses()
    {
        var now = DateTime.UtcNow;
        Assert.That(LightningAlertLogic.CooldownElapsed(now, now - TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)), Is.False);
    }

    [Test]
    public void CooldownElapsed_AfterCooldown_Fires()
    {
        var now = DateTime.UtcNow;
        Assert.That(LightningAlertLogic.CooldownElapsed(now, now - TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(5)), Is.True);
    }
}
