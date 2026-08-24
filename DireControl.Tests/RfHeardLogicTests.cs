using DireControl.Api.Services;
using DireControl.Data.Models;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Pure helpers behind the direct-RF reception rollups: day bucketing, median, position
/// selection, and own-station exclusion.
/// </summary>
[TestFixture]
public sealed class RfHeardLogicTests
{
    // =========================================================================
    // Median
    // =========================================================================

    [Test]
    public void Median_Empty_IsNull()
    {
        Assert.That(RfHeardLogic.Median([]), Is.Null);
    }

    [Test]
    public void Median_SingleValue_IsThatValue()
    {
        Assert.That(RfHeardLogic.Median([42.5]), Is.EqualTo(42.5));
    }

    [Test]
    public void Median_OddCount_IsMiddleValue()
    {
        Assert.That(RfHeardLogic.Median([30.0, 10.0, 20.0]), Is.EqualTo(20.0));
    }

    [Test]
    public void Median_EvenCount_IsMeanOfMiddlePair()
    {
        Assert.That(RfHeardLogic.Median([40.0, 10.0, 30.0, 20.0]), Is.EqualTo(25.0));
    }

    [Test]
    public void Median_DoesNotMutateInput()
    {
        var values = new List<double> { 30.0, 10.0, 20.0 };
        RfHeardLogic.Median(values);
        Assert.That(values, Is.EqualTo(new[] { 30.0, 10.0, 20.0 }));
    }

    // =========================================================================
    // LocalDay / LocalDayRangeUtc — buckets are LOCAL calendar days, so the chart
    // lines up with the operator's own "yesterday" rather than a UTC boundary.
    // =========================================================================

    [Test]
    public void LocalDay_MatchesLocalCalendarDate()
    {
        var utc = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        var expected = DateOnly.FromDateTime(utc.ToLocalTime());

        Assert.That(RfHeardLogic.LocalDay(utc), Is.EqualTo(expected));
    }

    [Test]
    public void LocalDay_TreatsUnspecifiedKindAsUtc()
    {
        // EF hands back DateTimes with Kind == Unspecified; they must not be reinterpreted
        // as local time, which would shift a packet into the wrong day.
        var utc = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        var unspecified = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Unspecified);

        Assert.That(RfHeardLogic.LocalDay(unspecified), Is.EqualTo(RfHeardLogic.LocalDay(utc)));
    }

    [Test]
    public void LocalDay_InstantsEitherSideOfLocalMidnight_LandInAdjacentDays()
    {
        var day = new DateOnly(2026, 8, 23);
        var (startUtc, endUtc) = RfHeardLogic.LocalDayRangeUtc(day);

        Assert.Multiple(() =>
        {
            Assert.That(RfHeardLogic.LocalDay(startUtc), Is.EqualTo(day));
            Assert.That(RfHeardLogic.LocalDay(startUtc.AddSeconds(-1)), Is.EqualTo(day.AddDays(-1)));
            Assert.That(RfHeardLogic.LocalDay(endUtc.AddSeconds(-1)), Is.EqualTo(day));
            Assert.That(RfHeardLogic.LocalDay(endUtc), Is.EqualTo(day.AddDays(1)));
        });
    }

    [Test]
    public void LocalDayRangeUtc_ConsecutiveDays_AbutWithoutOverlap()
    {
        var (_, endUtc) = RfHeardLogic.LocalDayRangeUtc(new DateOnly(2026, 8, 23));
        var (nextStartUtc, _) = RfHeardLogic.LocalDayRangeUtc(new DateOnly(2026, 8, 24));

        Assert.That(nextStartUtc, Is.EqualTo(endUtc));
    }

    // =========================================================================
    // PickPosition — the packet's own fix wins, because a mobile may have been
    // heard from somewhere other than where it is now.
    // =========================================================================

    [Test]
    public void PickPosition_PrefersPacketPositionOverStationLastKnown()
    {
        var pos = RfHeardLogic.PickPosition(1.0, 2.0, 10.0, 20.0);

        Assert.That(pos, Is.EqualTo((1.0, 2.0)));
    }

    [Test]
    public void PickPosition_FallsBackToStationLastKnown()
    {
        var pos = RfHeardLogic.PickPosition(null, null, 10.0, 20.0);

        Assert.That(pos, Is.EqualTo((10.0, 20.0)));
    }

    [Test]
    public void PickPosition_HalfAPacketFix_IsNotUsable()
    {
        // Latitude without longitude is not a position; fall through rather than invent one.
        var pos = RfHeardLogic.PickPosition(1.0, null, 10.0, 20.0);

        Assert.That(pos, Is.EqualTo((10.0, 20.0)));
    }

    [Test]
    public void PickPosition_NothingKnown_IsNull()
    {
        Assert.That(RfHeardLogic.PickPosition(null, null, null, null), Is.Null);
    }

    // =========================================================================
    // IsOwnStation — own beacons digipeated back to us arrive as ordinary RF
    // packets; counting them would make every radio look like it hears itself.
    // =========================================================================

    private static Radio MakeRadio(string callsign, string? ssid, int channel = 0) => new()
    {
        Name = $"Radio {channel}",
        Callsign = callsign,
        Ssid = ssid,
        ChannelNumber = channel,
    };

    [Test]
    public void IsOwnStation_MatchesConfiguredRadio()
    {
        var radios = new[] { MakeRadio("W3UWU", "9") };

        Assert.That(RfHeardLogic.IsOwnStation("W3UWU-9", radios, null), Is.True);
    }

    [Test]
    public void IsOwnStation_AppliesZeroSsidEquivalence()
    {
        var radios = new[] { MakeRadio("W3UWU", null) };

        Assert.Multiple(() =>
        {
            Assert.That(RfHeardLogic.IsOwnStation("W3UWU-0", radios, null), Is.True);
            Assert.That(RfHeardLogic.IsOwnStation("W3UWU", radios, null), Is.True);
        });
    }

    [Test]
    public void IsOwnStation_MatchesOurCallsignEvenWithNoRadios()
    {
        Assert.That(RfHeardLogic.IsOwnStation("N0CALL-10", [], "N0CALL-10"), Is.True);
    }

    [Test]
    public void IsOwnStation_DifferentSsid_IsSomeoneElse()
    {
        var radios = new[] { MakeRadio("W3UWU", "9") };

        Assert.That(RfHeardLogic.IsOwnStation("W3UWU-1", radios, null), Is.False);
    }

    [Test]
    public void IsOwnStation_UnrelatedCallsign_IsFalse()
    {
        var radios = new[] { MakeRadio("W3UWU", "9") };

        Assert.Multiple(() =>
        {
            Assert.That(RfHeardLogic.IsOwnStation("K4TUX-10", radios, "N0CALL-10"), Is.False);
            Assert.That(RfHeardLogic.IsOwnStation("", radios, "N0CALL-10"), Is.False);
        });
    }
}
