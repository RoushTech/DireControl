using DireControl.Modem.Ax25;
using DireControl.Modem.Ax25.Lapb;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>Timer recovery, RNR busy handling, T3 idle polls, resets, and modulo-128.</summary>
[TestFixture]
public sealed class LapbRecoveryAndBusyTests
{
    [Test]
    public void T1Expiry_EntersTimerRecoveryWithPoll()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();
        h.Machine.SendData([1]);
        h.TransmitAll();
        h.ClearRecordings();

        h.Machine.OnTimerExpired(LapbTimer.T1);

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.TimerRecovery));
        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.RR));
        Assert.That(h.LastSent.IsCommand, Is.True);
        Assert.That(h.LastSent.PollFinal, Is.True);
    }

    [Test]
    public void FinalResponse_ResyncsAndRetransmits()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();
        h.Machine.SendData([1]);
        h.TransmitAll();
        h.Machine.OnTimerExpired(LapbTimer.T1);
        h.TransmitAll();
        h.ClearRecordings();

        // Peer's final says it never saw our I frame (N(R)=0).
        h.Receive(Ax25FrameType.RR, nr: 0, pf: true);

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Connected));
        var resent = h.SentOfType(Ax25FrameType.I).Single();
        Assert.That(resent.Ns, Is.Zero);
    }

    [Test]
    public void FinalResponse_AllAcked_ReturnsToConnected()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();
        h.Machine.SendData([1]);
        h.TransmitAll();
        h.Machine.OnTimerExpired(LapbTimer.T1);
        h.TransmitAll();
        h.ClearRecordings();

        h.Receive(Ax25FrameType.RR, nr: 1, pf: true);

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Connected));
        Assert.That(h.Machine.OutstandingFrames, Is.Zero);
        Assert.That(h.SentOfType(Ax25FrameType.I), Is.Empty, "nothing to retransmit");
        Assert.That(h.TimerStops, Does.Contain(LapbTimer.T1));
    }

    [Test]
    public void N2Exhaustion_EmitsDmAndDisconnects()
    {
        var h = new LapbTestHarness(new LapbConfig { N2 = 2, T3 = TimeSpan.Zero });
        h.EstablishOutbound();
        h.Machine.SendData([1]);
        h.TransmitAll();

        h.Machine.OnTimerExpired(LapbTimer.T1); // → TimerRecovery, poll (retry 1)
        h.TransmitAll();
        h.Machine.OnTimerExpired(LapbTimer.T1); // retry 2
        h.TransmitAll();
        h.ClearRecordings();
        h.Machine.OnTimerExpired(LapbTimer.T1); // exceeds N2

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Disconnected));
        Assert.That(h.Transitions[^1].Reason, Is.EqualTo(LapbDisconnectReason.RetryExhausted));
        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.DM));
    }

    [Test]
    public void RemoteRnr_FreezesIFrames_RrThaws()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();

        h.Receive(Ax25FrameType.RNR, nr: 0);
        Assert.That(h.Machine.RemoteBusy, Is.True);

        h.Machine.SendData([1, 2, 3]);
        Assert.That(h.SentOfType(Ax25FrameType.I), Is.Empty, "remote busy blocks I frames");
        Assert.That(h.Machine.SendQueueDepth, Is.EqualTo(1));

        h.Receive(Ax25FrameType.RR, nr: 0);
        Assert.That(h.Machine.RemoteBusy, Is.False);
        Assert.That(h.SentOfType(Ax25FrameType.I).Count(), Is.EqualTo(1), "thawed queue drains");
    }

    [Test]
    public void RemoteRnr_WithNothingOutstanding_SchedulesBusyPoll()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();

        h.Receive(Ax25FrameType.RNR, nr: 0);

        Assert.That(h.T1StartedSinceClear, Is.True, "T1 must run to eventually poll a busy peer");
    }

    [Test]
    public void LocalBusy_EmitsRnrAndRefusesData()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();

        h.Machine.SetLocalBusy(true);
        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.RNR));

        h.ClearRecordings();
        h.Receive(Ax25FrameType.I, ns: 0, nr: 0, command: true, info: [1]);
        Assert.That(h.Delivered, Is.Empty, "busy: data not delivered");
        Assert.That(h.Machine.Vr, Is.Zero, "not acknowledged either — peer must resend");
        Assert.That(h.TimerStarts.Any(t => t.Timer == LapbTimer.T2), Is.False);

        // Poll while busy is answered RNR F=1.
        h.Receive(Ax25FrameType.I, ns: 0, nr: 0, pf: true, command: true, info: [1]);
        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.RNR));
        Assert.That(h.LastSent.PollFinal, Is.True);

        h.ClearRecordings();
        h.Machine.SetLocalBusy(false);
        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.RR), "clearing busy reopens the window");
    }

    [Test]
    public void T3Expiry_TriggersIdlePoll()
    {
        var h = new LapbTestHarness(new LapbConfig { T3 = TimeSpan.FromMinutes(5) });
        h.EstablishOutbound();

        h.Machine.OnTimerExpired(LapbTimer.T3);

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.TimerRecovery));
        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.RR));
        Assert.That(h.LastSent.PollFinal, Is.True);

        h.TransmitAll();
        h.Receive(Ax25FrameType.RR, nr: 0, pf: true);
        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Connected), "live link goes back to idle");
    }

    [Test]
    public void InvalidNr_TriggersLinkReset()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();

        // V(S)=0, so N(R)=5 acknowledges frames that were never sent.
        h.Receive(Ax25FrameType.RR, nr: 5);

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.AwaitingConnection));
        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.SABM));
        Assert.That(h.Transitions.Any(t => t.Reason == LapbDisconnectReason.ProtocolError), Is.True);
    }

    [Test]
    public void FrmrReceived_ResetsInsteadOfFrmrPingPong()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();

        h.Receive(Ax25FrameType.FRMR);

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.AwaitingConnection));
        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.SABM));
        Assert.That(h.SentOfType(Ax25FrameType.FRMR), Is.Empty, "we never send FRMR");
    }

    [Test]
    public void RepeatedResets_ExhaustBudget()
    {
        var h = new LapbTestHarness(new LapbConfig { N2 = 2, T3 = TimeSpan.Zero });
        h.EstablishOutbound();

        for (var i = 0; i < 2; i++)
        {
            h.Receive(Ax25FrameType.FRMR);
            Assert.That(h.Machine.State, Is.EqualTo(LapbState.AwaitingConnection));
            h.TransmitAll();
            h.Receive(Ax25FrameType.UA, pf: true);
            Assert.That(h.Machine.State, Is.EqualTo(LapbState.Connected));
        }

        h.Receive(Ax25FrameType.FRMR); // third reset exceeds the budget

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Disconnected));
        Assert.That(h.Transitions[^1].Reason, Is.EqualTo(LapbDisconnectReason.FrmrReceived));
    }

    [Test]
    public void Mod128_WindowBeyondSeven()
    {
        var h = new LapbTestHarness(new LapbConfig { WindowSize = 32, PacLen = 16, T3 = TimeSpan.Zero });
        h.EstablishInbound(extended: true);

        Assert.That(h.Machine.Modulus, Is.EqualTo(128));

        h.Machine.SendData(new byte[40 * 16]);

        Assert.That(h.SentOfType(Ax25FrameType.I).Count(), Is.EqualTo(32), "mod-128 window of 32");
        Assert.That(h.Machine.SendQueueDepth, Is.EqualTo(8));
    }

    [Test]
    public void Mod128_SequenceNumbersWrapCleanly()
    {
        var h = new LapbTestHarness(new LapbConfig { PacLen = 16, T3 = TimeSpan.Zero });
        h.EstablishInbound(extended: true);

        // 130 send/ack cycles walk V(S) across the 127→0 wrap.
        for (var i = 0; i < 130; i++)
        {
            h.Machine.SendData([1]);
            h.TransmitAll();
            h.Receive(Ax25FrameType.RR, nr: (i + 1) % 128);
        }

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Connected));
        Assert.That(h.Machine.OutstandingFrames, Is.Zero);
        Assert.That(h.Machine.Vs, Is.EqualTo(130 % 128));
        Assert.That(h.Transitions.Any(t => t.Reason == LapbDisconnectReason.ProtocolError), Is.False,
            "no spurious resets across the wrap");
    }

    [Test]
    public void Mod8_WindowClampedToSeven()
    {
        var h = new LapbTestHarness(new LapbConfig { WindowSize = 32, PacLen = 16, T3 = TimeSpan.Zero });
        h.EstablishOutbound(); // modulo-8

        h.Machine.SendData(new byte[10 * 16]);

        Assert.That(h.SentOfType(Ax25FrameType.I).Count(), Is.EqualTo(7),
            "k can never reach the modulus");
    }
}
