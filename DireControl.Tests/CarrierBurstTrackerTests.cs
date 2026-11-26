using DireControl.Api.Services;
using NUnit.Framework;

namespace DireControl.Tests;

[TestFixture]
public sealed class CarrierBurstTrackerTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void NoBlocks_EverythingIsNull()
    {
        var t = new CarrierBurstTracker();
        Assert.Multiple(() =>
        {
            Assert.That(t.LevelUpdatedAt, Is.Null);
            Assert.That(t.CarrierSince, Is.Null);
            Assert.That(t.PreamblesSinceCarrier(10), Is.Zero);
        });
    }

    [Test]
    public void EveryBlock_StampsLevelUpdatedAt()
    {
        var t = new CarrierBurstTracker();
        t.OnBlock(false, 0, T0);
        t.OnBlock(false, 0, T0.AddMilliseconds(21));
        Assert.That(t.LevelUpdatedAt, Is.EqualTo(T0.AddMilliseconds(21)));
    }

    [Test]
    public void CarrierSince_IsTheBurstStart_AndClearsWhenCarrierDrops()
    {
        var t = new CarrierBurstTracker();
        t.OnBlock(true, 5, T0);
        t.OnBlock(true, 5, T0.AddSeconds(3));
        Assert.That(t.CarrierSince, Is.EqualTo(T0), "held carrier keeps its start time");

        t.OnBlock(false, 0, T0.AddSeconds(4));
        Assert.That(t.CarrierSince, Is.Null);
    }

    [Test]
    public void PreamblesSinceCarrier_CountsOnlyThisBurst()
    {
        var t = new CarrierBurstTracker();
        t.OnBlock(true, 40, T0);                 // 40 preambles already seen in earlier bursts
        Assert.That(t.PreamblesSinceCarrier(40), Is.Zero, "a held carrier with no new preamble is noise");
        Assert.That(t.PreamblesSinceCarrier(42), Is.EqualTo(2));

        t.OnBlock(false, 0, T0.AddSeconds(1));
        Assert.That(t.PreamblesSinceCarrier(42), Is.Zero, "clear channel reports zero");
    }

    [Test]
    public void Reset_ForgetsThePreviousSession()
    {
        var t = new CarrierBurstTracker();
        t.OnBlock(true, 1, T0);
        t.Reset();
        Assert.Multiple(() =>
        {
            Assert.That(t.LevelUpdatedAt, Is.Null);
            Assert.That(t.CarrierSince, Is.Null);
        });
    }

    [Test]
    public void CarrierDuty_IsTheFractionOfTheLastMinuteWithCarrier()
    {
        var t = new CarrierBurstTracker();
        Assert.That(t.CarrierDutyLastMinute(T0), Is.Null, "no audio yet");

        // 40 s quiet, then 20 s of carrier, 1000 samples per second.
        for (var sec = 0; sec < 60; sec++)
            t.OnBlock(sec >= 40, 0, T0.AddSeconds(sec), samples: 1000);

        Assert.That(t.CarrierDutyLastMinute(T0.AddSeconds(59)), Is.EqualTo(20.0 / 60).Within(1e-9));
    }

    [Test]
    public void CarrierDuty_ForgetsAudioOlderThanAMinute()
    {
        var t = new CarrierBurstTracker();
        for (var sec = 0; sec < 30; sec++)
            t.OnBlock(true, 0, T0.AddSeconds(sec), samples: 1000);
        for (var sec = 90; sec < 120; sec++)
            t.OnBlock(false, 0, T0.AddSeconds(sec), samples: 1000);

        Assert.That(t.CarrierDutyLastMinute(T0.AddSeconds(119)), Is.Zero, "the held minute has rolled out");
        Assert.That(t.CarrierDutyLastMinute(T0.AddSeconds(300)), Is.Null, "nothing in the window");
    }
}
