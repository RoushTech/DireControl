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
    public void Evaluate_StrikeWithinRadius_Alerts()
    {
        var now = DateTime.UtcNow;

        var (shouldAlert, distanceKm) = LightningAlertLogic.Evaluate(
            At(35.1, -85), 35, -85, 50, now, null, TimeSpan.FromMinutes(5), null);

        Assert.That(shouldAlert, Is.True);
        Assert.That(distanceKm, Is.EqualTo(11.1).Within(0.2));
    }

    [Test]
    public void Evaluate_StrikeOutsideRadius_DoesNotAlert()
    {
        var now = DateTime.UtcNow;

        var (shouldAlert, distanceKm) = LightningAlertLogic.Evaluate(
            At(36.0, -85), 35, -85, 50, now, null, TimeSpan.FromMinutes(5), null);

        Assert.That(shouldAlert, Is.False);
        Assert.That(distanceKm, Is.EqualTo(111.2).Within(0.5));
    }

    [Test]
    public void Evaluate_WithinCooldown_DoesNotAlert()
    {
        var now = DateTime.UtcNow;

        var (shouldAlert, _) = LightningAlertLogic.Evaluate(
            At(35.1, -85), 35, -85, 50, now, now - TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), null);

        Assert.That(shouldAlert, Is.False);
    }

    [Test]
    public void Evaluate_AfterCooldown_Alerts()
    {
        var now = DateTime.UtcNow;

        var (shouldAlert, _) = LightningAlertLogic.Evaluate(
            At(35.1, -85), 35, -85, 50, now, now - TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(5), null);

        Assert.That(shouldAlert, Is.True);
    }

    /// <summary>The feed can redeliver a strike; it must not alert twice even with no cooldown.</summary>
    [Test]
    public void Evaluate_SameStrikeRedelivered_DoesNotAlertTwice()
    {
        var now = DateTime.UtcNow;
        var strike = new LightningStrike(now.AddSeconds(-3), 35.1, -85);

        var (first, _) = LightningAlertLogic.Evaluate(
            strike, 35, -85, 50, now, null, TimeSpan.Zero, null);
        var (second, _) = LightningAlertLogic.Evaluate(
            strike, 35, -85, 50, now, now, TimeSpan.Zero, strike);

        Assert.That(first, Is.True);
        Assert.That(second, Is.False);
    }

    /// <summary>A different strike with a zero cooldown is a new alert, not a duplicate.</summary>
    [Test]
    public void Evaluate_DifferentStrikeWithNoCooldown_Alerts()
    {
        var now = DateTime.UtcNow;
        var previous = new LightningStrike(now.AddSeconds(-30), 35.1, -85);
        var latest = new LightningStrike(now.AddSeconds(-1), 35.12, -85.02);

        var (shouldAlert, _) = LightningAlertLogic.Evaluate(
            latest, 35, -85, 50, now, now.AddSeconds(-30), TimeSpan.Zero, previous);

        Assert.That(shouldAlert, Is.True);
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
