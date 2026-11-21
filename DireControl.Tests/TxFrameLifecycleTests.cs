using Microsoft.Extensions.DependencyInjection;
using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using DireControl.Enums;
using DireControl.Modem.Ax25;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// The TX lifecycle the UI shows: every queued frame gets an id
/// and moves Queued → WaitingForChannel → OnAir → Sent, or ends Failed /
/// Dropped with a reason. These drive the same registry calls, reasons and
/// lane the modem's transmit loop uses.
/// </summary>
[TestFixture]
public sealed class TxFrameLifecycleTests
{
    private static readonly byte[] Frame = Ax25Encoder.EncodeUiFrame("N0CALL-1", "!4000.00N/10500.00W-test", "WIDE1-1");

    private static (TxFrameRegistry Registry, List<TxFrameDto> Events) Create()
    {
        var registry = new TxFrameRegistry(TimeProvider.System);
        var events = new List<TxFrameDto>();
        registry.Changed += events.Add;
        return (registry, events);
    }

    private static long Queue(TxFrameRegistry r, string radioId = "r1")
    {
        var id = r.Begin(new TxFrameInfo(TxFrameKind.Beacon, "Position beacon"), Frame);
        r.Queued(id, radioId, "FT-8100R");
        return id;
    }

    [Test]
    public void Success_QueuedWaitingOnAirSent()
    {
        var (r, events) = Create();
        var id = Queue(r);
        r.WaitingForChannel("r1");
        r.Report(id, TxFrameState.OnAir);
        r.Report(id, TxFrameState.Sent);

        Assert.Multiple(() =>
        {
            Assert.That(events.Select(e => e.State), Is.EqualTo(new[]
            {
                TxFrameState.Queued, TxFrameState.WaitingForChannel, TxFrameState.OnAir, TxFrameState.Sent,
            }));
            Assert.That(events.All(e => e.Id == id && e.RadioId == "r1"), Is.True);
            var last = events[^1];
            Assert.That(last.WaitingSince, Is.Not.Null);
            Assert.That(last.OnAirAt, Is.Not.Null);
            Assert.That(last.SentAt, Is.Not.Null);
            Assert.That(last.Reason, Is.Null);
            Assert.That(last.Detail, Does.StartWith("N0CALL-1>"), "TNC2 detail for the TX queue");
        });
    }

    [Test]
    public void CsmaTimeout_FailsWithTheDiscardReason()
    {
        var (r, events) = Create();
        var id = Queue(r);
        r.WaitingForChannel("r1");
        r.Report(id, TxFrameState.Failed, SoundModemService.CsmaDiscardReason(TimeSpan.FromSeconds(20)));

        var last = events[^1];
        Assert.Multiple(() =>
        {
            Assert.That(events.Select(e => e.State), Is.EqualTo(new[]
            {
                TxFrameState.Queued, TxFrameState.WaitingForChannel, TxFrameState.Failed,
            }));
            Assert.That(last.Reason, Does.Contain("Waited 20 s for a clear channel"));
            Assert.That(last.OnAirAt, Is.Null, "a discarded frame never went to air");
        });
    }

    [Test]
    public void LaneOverflow_DropsTheOldestFrame_WithAReason()
    {
        var (r, events) = Create();
        var lane = SoundModemService.CreateDroppingLane(2, r, "normal", _ => { });
        var ids = Enumerable.Range(0, 3).Select(_ => Queue(r)).ToList();
        var completions = ids.Select(_ => new TaskCompletionSource<bool>()).ToList();
        for (var i = 0; i < 3; i++)
            Assert.That(lane.Writer.TryWrite(new TxItem(Frame, completions[i], ids[i])), Is.True);

        var dropped = events.Single(e => e.State == TxFrameState.Dropped);
        Assert.Multiple(() =>
        {
            Assert.That(dropped.Id, Is.EqualTo(ids[0]), "DropOldest");
            Assert.That(dropped.Reason, Does.Contain("normal TX queue was full"));
            Assert.That(completions[0].Task.IsCanceled, Is.True, "LAPB still sees the drop");
            Assert.That(r.Get(ids[1])!.State, Is.EqualTo(TxFrameState.Queued));
        });
    }

    [Test]
    public void PttFault_FailsAfterOnAir_SayingTheRadioMayStillTransmit()
    {
        var (r, events) = Create();
        var id = Queue(r);
        r.Report(id, TxFrameState.OnAir);
        var reason = SoundModemService.KeyupFailureReason(
            new KeyupResult(KeyupOutcome.UnkeyFailed, new IOException("rigctld timeout")));
        r.Report(id, TxFrameState.Failed, reason);

        Assert.Multiple(() =>
        {
            Assert.That(events[^1].State, Is.EqualTo(TxFrameState.Failed));
            Assert.That(reason, Does.StartWith("PTT could not be released (rigctld timeout)"));
            Assert.That(reason, Does.Contain("may still be transmitting"));
        });
    }

    [Test]
    public void OpenFailure_NeverOnAir_FrameNotSent()
    {
        var reason = SoundModemService.KeyupFailureReason(new KeyupResult(KeyupOutcome.OpenFailed));
        Assert.That(reason, Is.EqualTo("Playback device could not be opened. Frame not sent."));
    }

    [Test]
    public void FinalStates_AreNeverOverwritten()
    {
        var (r, events) = Create();
        var id = Queue(r);
        r.Report(id, TxFrameState.Failed, "nope");
        r.Report(id, TxFrameState.Sent);
        r.Report(id, TxFrameState.OnAir);

        Assert.That(r.Get(id)!.State, Is.EqualTo(TxFrameState.Failed));
        Assert.That(events, Has.Count.EqualTo(2));
    }

    [Test]
    public void OnAirBeforeQueued_KeepsOnAir_AndStillRecordsTheRadio()
    {
        // The TX thread can report before the enqueuing thread reports Queued.
        var (r, _) = Create();
        var id = r.Begin(new TxFrameInfo(TxFrameKind.Message, "Message to K4ABC", "42"), Frame);
        r.Report(id, TxFrameState.OnAir);
        r.Queued(id, "r1", "FT-8100R");

        var dto = r.Get(id)!;
        Assert.Multiple(() =>
        {
            Assert.That(dto.State, Is.EqualTo(TxFrameState.OnAir));
            Assert.That(dto.RadioId, Is.EqualTo("r1"));
        });
    }

    [Test]
    public void ModemStop_FailsEverythingPendingForThatRadioOnly()
    {
        var (r, _) = Create();
        var a = Queue(r, "r1");
        var b = Queue(r, "r2");
        var sent = Queue(r, "r1");
        r.Report(sent, TxFrameState.Sent);

        r.FailPending("r1", "The modem restarted before this frame went to air.");

        Assert.Multiple(() =>
        {
            Assert.That(r.Get(a)!.State, Is.EqualTo(TxFrameState.Failed));
            Assert.That(r.Get(a)!.Reason, Does.Contain("restarted"));
            Assert.That(r.Get(b)!.State, Is.EqualTo(TxFrameState.Queued));
            Assert.That(r.Get(sent)!.State, Is.EqualTo(TxFrameState.Sent));
        });
    }

    [Test]
    public void AbandonedFrames_AreNeverPublished_AndRecentIsNewestFirst()
    {
        var (r, events) = Create();
        var first = Queue(r);
        var abandoned = r.Begin(new TxFrameInfo(TxFrameKind.Beacon, "x"), Frame);
        r.Abandon(abandoned);
        var second = Queue(r);

        Assert.Multiple(() =>
        {
            Assert.That(events.Any(e => e.Id == abandoned), Is.False);
            Assert.That(r.Recent().Select(d => d.Id), Is.EqualTo(new[] { second, first }));
        });
    }

    [Test]
    public void LatestByRef_FindsTheNewestFrameForAMessage()
    {
        var (r, _) = Create();
        var original = r.Begin(new TxFrameInfo(TxFrameKind.Message, "Message to K4ABC", "7"), Frame);
        r.Queued(original, "r1", "FT");
        var retry = r.Begin(new TxFrameInfo(TxFrameKind.Message, "Message to K4ABC (retry)", "7"), Frame);
        r.Queued(retry, "r1", "FT");

        Assert.That(r.LatestByRef(TxFrameKind.Message, "7")!.Id, Is.EqualTo(retry));
    }

    [Test]
    public void Window_IsBounded()
    {
        var (r, _) = Create();
        for (var i = 0; i < TxFrameRegistry.Capacity + 25; i++)
            Queue(r);
        Assert.That(r.Recent(1000), Has.Count.EqualTo(TxFrameRegistry.Capacity));
    }

    [Test]
    public void FrameTransmitService_RejectedFrame_ReturnsNullAndPublishesNothing()
    {
        var (r, events) = Create();
        var svc = new FrameTransmitService(
            new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(),
            new KissConnectionHolder(),
            r,
            Microsoft.Extensions.Options.Options.Create(new DireControlOptions { OurCallsign = "N7DCX-10" }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FrameTransmitService>.Instance);

        var id = svc.Send(new byte[600], 0, new TxFrameInfo(TxFrameKind.Kiss, "KISS client frame"));

        Assert.Multiple(() =>
        {
            Assert.That(id, Is.Null);
            Assert.That(events, Is.Empty);
            Assert.That(r.Recent(), Is.Empty);
        });
    }

    [TestCase("N0CALL")]
    [TestCase("n0call-10")]
    [TestCase("")]
    public void N0Call_BlocksEveryTransmit_BeforeAnyBackendIsTouched(string callsign)
    {
        var (r, events) = Create();
        // An empty provider: resolving the modem would throw, proving the block comes first.
        var svc = new FrameTransmitService(
            new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(),
            new KissConnectionHolder(),
            r,
            Microsoft.Extensions.Options.Options.Create(new DireControlOptions { OurCallsign = callsign }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FrameTransmitService>.Instance);
        var completion = new TaskCompletionSource<bool>();

        var id = svc.Send(Frame, 0, new TxFrameInfo(TxFrameKind.Beacon, "Position beacon"), txCompletion: completion);

        Assert.Multiple(() =>
        {
            Assert.That(id, Is.Null);
            Assert.That(events, Is.Empty);
            Assert.That(completion.Task.IsCanceled, Is.True);
            Assert.That(StationIdentityLogic.TransmitBlockedReason(callsign), Does.Contain("N0CALL"));
        });
    }

    [Test]
    public void RealCallsign_IsNotBlocked() =>
        Assert.That(StationIdentityLogic.TransmitBlockedReason("N7DCX-10"), Is.Null);

    [Test]
    public async Task WaitForFinal_CompletesWhenTheFrameIsSent_AndTimesOutOtherwise()
    {
        var (r, _) = Create();
        var id = Queue(r);
        var waiting = r.WaitForFinalAsync(id, TimeSpan.FromSeconds(5));
        r.Report(id, TxFrameState.OnAir);
        r.Report(id, TxFrameState.Sent);
        Assert.That((await waiting)!.State, Is.EqualTo(TxFrameState.Sent));

        var stuck = Queue(r);
        Assert.That(await r.WaitForFinalAsync(stuck, TimeSpan.FromMilliseconds(50)), Is.Null);
    }
}
