using DireControl.Api.Services;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Scheduling decisions for automatic beaconing: never-beaconed radios, elapsed
/// vs not-yet-elapsed intervals, the interval floor, and attempt-vs-beacon
/// throttling so a misconfigured radio is not retried on every poll.
/// </summary>
[TestFixture]
public sealed class AutoBeaconLogicTests
{
    private static readonly DateTime Now = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void NeverBeaconedAndNeverAttempted_IsDue()
    {
        Assert.That(AutoBeaconLogic.IsDue(Now, null, null, 600), Is.True);
    }

    [Test]
    public void IntervalElapsedSinceLastBeacon_IsDue()
    {
        var lastBeacon = Now.AddSeconds(-601);
        Assert.That(AutoBeaconLogic.IsDue(Now, lastBeacon, null, 600), Is.True);
    }

    [Test]
    public void ExactlyIntervalSinceLastBeacon_IsDue()
    {
        var lastBeacon = Now.AddSeconds(-600);
        Assert.That(AutoBeaconLogic.IsDue(Now, lastBeacon, null, 600), Is.True);
    }

    [Test]
    public void IntervalNotYetElapsedSinceLastBeacon_IsNotDue()
    {
        var lastBeacon = Now.AddSeconds(-599);
        Assert.That(AutoBeaconLogic.IsDue(Now, lastBeacon, null, 600), Is.False);
    }

    [Test]
    public void RecentAttemptWithNoSuccessfulBeacon_ThrottlesUntilInterval()
    {
        // Radio cannot beacon (e.g. no home position); it was just attempted.
        var lastAttempt = Now.AddSeconds(-30);
        Assert.That(AutoBeaconLogic.IsDue(Now, null, lastAttempt, 600), Is.False);
    }

    [Test]
    public void FailedAttemptOlderThanInterval_IsDueAgain()
    {
        var lastAttempt = Now.AddSeconds(-601);
        Assert.That(AutoBeaconLogic.IsDue(Now, null, lastAttempt, 600), Is.True);
    }

    [Test]
    public void MostRecentOfBeaconAndAttemptGoverns()
    {
        // Beacon is old, but a more recent attempt should still throttle.
        var oldBeacon = Now.AddSeconds(-5000);
        var recentAttempt = Now.AddSeconds(-10);
        Assert.That(AutoBeaconLogic.IsDue(Now, oldBeacon, recentAttempt, 600), Is.False);
    }

    [Test]
    public void IntervalBelowFloor_IsClampedToMinimum()
    {
        // Interval of 10 s is clamped to the 60 s floor: 30 s elapsed is not due.
        var lastBeacon = Now.AddSeconds(-30);
        Assert.That(AutoBeaconLogic.IsDue(Now, lastBeacon, null, 10), Is.False);

        // 60 s elapsed clears the floor.
        var older = Now.AddSeconds(-60);
        Assert.That(AutoBeaconLogic.IsDue(Now, older, null, 10), Is.True);
    }
}
