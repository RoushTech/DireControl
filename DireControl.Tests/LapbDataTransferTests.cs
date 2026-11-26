using System.Text;
using DireControl.Modem.Ax25;
using DireControl.Modem.Ax25.Lapb;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>I-frame flow: segmentation, windowing, T1/T2 discipline, REJ recovery.</summary>
[TestFixture]
public sealed class LapbDataTransferTests
{
    [Test]
    public void SendData_SegmentsToPacLen()
    {
        var h = new LapbTestHarness(new LapbConfig { PacLen = 128, T3 = TimeSpan.Zero });
        h.EstablishOutbound();

        h.Machine.SendData(new byte[300]);

        var iFrames = h.SentOfType(Ax25FrameType.I).ToList();
        Assert.That(iFrames, Has.Count.EqualTo(3));
        Assert.That(iFrames.Select(f => f.Ns), Is.EqualTo([0, 1, 2]).AsCollection);
        Assert.That(iFrames.Select(f => f.Info.Length), Is.EqualTo([128, 128, 44]).AsCollection);
        Assert.That(iFrames.All(f => f.IsCommand), Is.True);
    }

    [Test]
    public void Window_HoldsFramesUntilAcked()
    {
        var h = new LapbTestHarness(new LapbConfig { WindowSize = 4, PacLen = 128, T3 = TimeSpan.Zero });
        h.EstablishOutbound();

        h.Machine.SendData(new byte[5 * 128]);

        Assert.That(h.SentOfType(Ax25FrameType.I).Count(), Is.EqualTo(4), "k=4 caps the burst");
        Assert.That(h.Machine.SendQueueDepth, Is.EqualTo(1));

        h.TransmitAll();
        h.Receive(Ax25FrameType.RR, nr: 1);

        Assert.That(h.SentOfType(Ax25FrameType.I).Count(), Is.EqualTo(5), "ack frees the window");
        Assert.That(h.SentOfType(Ax25FrameType.I).Last().Ns, Is.EqualTo(4));
        Assert.That(h.Machine.SendQueueDepth, Is.Zero);
    }

    [Test]
    public void T1_StartsOnTransmitReport_NotOnEnqueue()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();

        h.Machine.SendData("hello"u8);
        Assert.That(h.T1StartedSinceClear, Is.False,
            "T1 before airtime would retransmit into CSMA queue delays");

        h.TransmitAll();
        Assert.That(h.T1StartedSinceClear, Is.True);
    }

    [Test]
    public void InSequenceRx_DeliversAndDelayedAcks()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();

        h.Receive(Ax25FrameType.I, ns: 0, nr: 0, command: true, info: Encoding.ASCII.GetBytes("hi"));

        Assert.That(h.Delivered, Has.Count.EqualTo(1));
        Assert.That(Encoding.ASCII.GetString(h.Delivered[0]), Is.EqualTo("hi"));
        Assert.That(h.Machine.Vr, Is.EqualTo(1));
        Assert.That(h.TimerStarts.Any(t => t.Timer == LapbTimer.T2), Is.True, "ack is delayed via T2");
        Assert.That(h.Sent, Is.Empty, "no immediate RR");

        h.Machine.OnTimerExpired(LapbTimer.T2);

        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.RR));
        Assert.That(h.LastSent.Nr, Is.EqualTo(1));
        Assert.That(h.LastSent.IsCommand, Is.False);
    }

    [Test]
    public void OutboundIFrame_PiggybacksAckAndCancelsT2()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();

        h.Receive(Ax25FrameType.I, ns: 0, nr: 0, command: true, info: [1]);
        Assert.That(h.TimerStarts.Any(t => t.Timer == LapbTimer.T2), Is.True);

        h.Machine.SendData([2]);

        var i = h.SentOfType(Ax25FrameType.I).Single();
        Assert.That(i.Nr, Is.EqualTo(1), "I frame carries the owed N(R)");
        Assert.That(h.TimerStops, Does.Contain(LapbTimer.T2));

        h.Machine.OnTimerExpired(LapbTimer.T2); // stale expiry after cancel is a no-op
        Assert.That(h.SentOfType(Ax25FrameType.RR), Is.Empty);
    }

    [Test]
    public void OutOfSequenceRx_SendsExactlyOneRej()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();

        h.Receive(Ax25FrameType.I, ns: 2, nr: 0, command: true, info: [1]);
        h.Receive(Ax25FrameType.I, ns: 3, nr: 0, command: true, info: [2]);

        Assert.That(h.Delivered, Is.Empty);
        var rejs = h.SentOfType(Ax25FrameType.REJ).ToList();
        Assert.That(rejs, Has.Count.EqualTo(1), "REJ repeats until recovery would flood the channel");
        Assert.That(rejs[0].Nr, Is.Zero);

        // Recovery: the expected frame arrives, REJ latch resets.
        h.Receive(Ax25FrameType.I, ns: 0, nr: 0, command: true, info: [3]);
        Assert.That(h.Delivered, Has.Count.EqualTo(1));
        h.Receive(Ax25FrameType.I, ns: 4, nr: 0, command: true, info: [4]);
        Assert.That(h.SentOfType(Ax25FrameType.REJ).Count(), Is.EqualTo(2), "new gap, new REJ");
    }

    [Test]
    public void RejRx_RewindsAndRetransmits()
    {
        var h = new LapbTestHarness(new LapbConfig { PacLen = 16, T3 = TimeSpan.Zero });
        h.EstablishOutbound();
        h.Machine.SendData(new byte[3 * 16]);
        h.TransmitAll();
        h.ClearRecordings();

        h.Receive(Ax25FrameType.REJ, nr: 1);

        var resent = h.SentOfType(Ax25FrameType.I).ToList();
        Assert.That(resent.Select(f => f.Ns), Is.EqualTo([1, 2]).AsCollection);
        Assert.That(h.Machine.Vs, Is.EqualTo(3));
        Assert.That(h.Machine.Va, Is.EqualTo(1));
    }

    [Test]
    public void ICommandWithPoll_GetsImmediateFinal()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();

        h.Receive(Ax25FrameType.I, ns: 0, nr: 0, pf: true, command: true, info: [1]);

        Assert.That(h.Delivered, Has.Count.EqualTo(1));
        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.RR));
        Assert.That(h.LastSent.PollFinal, Is.True);
        Assert.That(h.LastSent.Nr, Is.EqualTo(1));
        Assert.That(h.TimerStarts.Any(t => t.Timer == LapbTimer.T2), Is.False, "final replaces delayed ack");
    }

    [Test]
    public void FullAck_StopsT1()
    {
        var h = new LapbTestHarness(new LapbConfig { PacLen = 16, T3 = TimeSpan.FromMinutes(5) });
        h.EstablishOutbound();
        h.Machine.SendData(new byte[32]);
        h.TransmitAll();
        h.ClearRecordings();

        h.Receive(Ax25FrameType.RR, nr: 2);

        Assert.That(h.Machine.Va, Is.EqualTo(2));
        Assert.That(h.Machine.OutstandingFrames, Is.Zero);
        Assert.That(h.TimerStops, Does.Contain(LapbTimer.T1));
        Assert.That(h.TimerStarts.Any(t => t.Timer == LapbTimer.T3), Is.True, "idle: back on T3");
    }

    [Test]
    public void PartialAck_RestartsT1()
    {
        var h = new LapbTestHarness(new LapbConfig { PacLen = 16, T3 = TimeSpan.Zero });
        h.EstablishOutbound();
        h.Machine.SendData(new byte[32]);
        h.TransmitAll();
        h.ClearRecordings();

        h.Receive(Ax25FrameType.RR, nr: 1);

        Assert.That(h.Machine.Va, Is.EqualTo(1));
        Assert.That(h.Machine.OutstandingFrames, Is.EqualTo(1));
        Assert.That(h.T1StartedSinceClear, Is.True, "outstanding frame still needs its timer");
    }
}
