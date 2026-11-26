using DireControl.Modem.Ax25;
using DireControl.Modem.Ax25.Lapb;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>Link teardown: DISC/UA, remote DISC and DM, unanswered DISC, abort.</summary>
[TestFixture]
public sealed class LapbDisconnectTests
{
    [Test]
    public void Disconnect_SendsDisc_UaCompletes()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();

        h.Machine.Disconnect();

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.AwaitingRelease));
        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.DISC));
        Assert.That(h.LastSent.PollFinal, Is.True);

        h.TransmitAll();
        h.Receive(Ax25FrameType.UA, pf: true);

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Disconnected));
        Assert.That(h.Transitions[^1].Reason, Is.EqualTo(LapbDisconnectReason.LocalRequest));
    }

    [Test]
    public void Disconnect_DmAlsoCompletes()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();
        h.Machine.Disconnect();
        h.TransmitAll();

        h.Receive(Ax25FrameType.DM, pf: true);

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Disconnected));
    }

    [Test]
    public void RemoteDisc_AnswersUaAndDisconnects()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();

        h.Receive(Ax25FrameType.DISC, pf: true, command: true);

        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.UA));
        Assert.That(h.LastSent.PollFinal, Is.True);
        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Disconnected));
        Assert.That(h.Transitions[^1].Reason, Is.EqualTo(LapbDisconnectReason.RemoteDisc));
    }

    [Test]
    public void RemoteDm_DisconnectsSilently()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();

        h.Receive(Ax25FrameType.DM);

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Disconnected));
        Assert.That(h.Transitions[^1].Reason, Is.EqualTo(LapbDisconnectReason.RemoteDm));
        Assert.That(h.Sent, Is.Empty, "DM gets no response");
    }

    [Test]
    public void UnansweredDisc_TimesOutViaN2()
    {
        var h = new LapbTestHarness(new LapbConfig { N2 = 2, T3 = TimeSpan.Zero });
        h.EstablishOutbound();
        h.Machine.Disconnect();
        h.TransmitAll();

        h.Machine.OnTimerExpired(LapbTimer.T1); // retry 1: DISC again
        h.TransmitAll();
        Assert.That(h.SentOfType(Ax25FrameType.DISC).Count(), Is.EqualTo(2));

        h.Machine.OnTimerExpired(LapbTimer.T1); // retry 2
        h.TransmitAll();
        h.Machine.OnTimerExpired(LapbTimer.T1); // exceeds N2 — give up, link is dead anyway

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Disconnected));
        Assert.That(h.Transitions[^1].Reason, Is.EqualTo(LapbDisconnectReason.LocalRequest));
    }

    [Test]
    public void Abort_EmitsDmAndDropsEverything()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();
        h.Machine.SendData(new byte[300]);
        h.ClearRecordings();

        h.Machine.Abort();

        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.DM));
        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Disconnected));
        Assert.That(h.Machine.SendQueueDepth, Is.Zero);
    }

    [Test]
    public void DiscDuringAwaitingConnection_GetsDm()
    {
        var h = new LapbTestHarness();
        h.Machine.Connect();
        h.ClearRecordings();

        h.Receive(Ax25FrameType.DISC, pf: true, command: true);

        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.DM));
        Assert.That(h.Machine.State, Is.EqualTo(LapbState.AwaitingConnection), "still trying to connect");
    }

    [Test]
    public void CommandPollDuringAwaitingRelease_GetsDm()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();
        h.Machine.Disconnect();
        h.ClearRecordings();

        h.Receive(Ax25FrameType.I, ns: 0, nr: 0, pf: true, command: true, info: [1]);

        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.DM));
        Assert.That(h.Delivered, Is.Empty, "no data accepted while releasing");
    }
}
