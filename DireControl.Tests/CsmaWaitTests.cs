using DireControl.Api.Services;
using DireControl.Tests.Fakes;
using NUnit.Framework;
using static DireControl.Api.Services.SoundModemService;

namespace DireControl.Tests;

/// <summary>
/// The p-persistence CSMA wait, driven on a virtual clock.  A carrier detect
/// that never clears used to stall the transmitter forever.
/// </summary>
[TestFixture]
public sealed class CsmaWaitTests
{
    private static Task<CsmaResult> Wait(
        Func<bool> busy, int persistence, AutoAdvanceTimeProvider clock, Random? rng = null, int maxSeconds = 20) =>
        WaitForClearChannelAsync(
            busy, persistence, slotMs: 100, TimeSpan.FromSeconds(maxSeconds),
            rng ?? new Random(7), clock, CancellationToken.None);

    [Test]
    public async Task ChannelNeverClears_TimesOutAtCap()
    {
        var clock = new AutoAdvanceTimeProvider();

        var result = await Wait(() => true, persistence: 63, clock);

        Assert.That(result, Is.EqualTo(CsmaResult.TimedOut));
        Assert.That(clock.Elapsed, Is.EqualTo(TimeSpan.FromSeconds(20)).Within(TimeSpan.FromMilliseconds(20)));
    }

    [Test]
    public async Task BusyThenClear_TransmitsAfterCarrierDrops()
    {
        var clock = new AutoAdvanceTimeProvider();

        var result = await Wait(() => clock.Elapsed < TimeSpan.FromSeconds(3), persistence: 255, clock);

        Assert.That(result, Is.EqualTo(CsmaResult.Clear));
        Assert.That(clock.Elapsed, Is.EqualTo(TimeSpan.FromSeconds(3)).Within(TimeSpan.FromMilliseconds(20)));
    }

    [Test]
    public async Task Persistence255_TransmitsImmediatelyOnClearChannel()
    {
        var clock = new AutoAdvanceTimeProvider();

        var result = await Wait(() => false, persistence: 255, clock);

        Assert.That(result, Is.EqualTo(CsmaResult.Clear));
        Assert.That(clock.Delays, Is.Empty);
    }

    [Test]
    public async Task Persistence0_WaitsSlotsButNeverTimesOutOnClearChannel()
    {
        // P = 0 transmits with probability 1/256 per slot — a long wait on a
        // clear channel is legitimate and must not count against the cap.
        var clock = new AutoAdvanceTimeProvider();

        var result = await Wait(() => false, persistence: 0, clock, new Random(42), maxSeconds: 1);

        Assert.That(result, Is.EqualTo(CsmaResult.Clear));
        Assert.That(clock.Delays, Is.Not.Empty);
        Assert.That(clock.Delays, Has.All.EqualTo(TimeSpan.FromMilliseconds(100)));
    }
}
