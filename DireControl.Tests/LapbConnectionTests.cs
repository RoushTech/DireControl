using DireControl.Modem.Ax25;
using DireControl.Modem.Ax25.Lapb;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>Link establishment: SABM/SABME, UA, DM fallback, inbound accept, resets.</summary>
[TestFixture]
public sealed class LapbConnectionTests
{
    [Test]
    public void Connect_SendsSabmPoll_T1OnlyAfterTransmit()
    {
        var h = new LapbTestHarness();
        h.Machine.Connect();

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.AwaitingConnection));
        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.SABM));
        Assert.That(h.LastSent.PollFinal, Is.True);
        Assert.That(h.LastSent.IsCommand, Is.True);
        Assert.That(h.T1StartedSinceClear, Is.False, "T1 must not run until the frame is on air");

        h.TransmitAll();
        Assert.That(h.T1StartedSinceClear, Is.True);
    }

    [Test]
    public void Connect_UaCompletesHandshake()
    {
        var h = new LapbTestHarness();
        h.Machine.Connect();
        h.TransmitAll();
        h.Receive(Ax25FrameType.UA, pf: true);

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Connected));
        Assert.That(h.Machine.ExtendedMode, Is.False);
        Assert.That(h.Machine.Modulus, Is.EqualTo(8));
        Assert.That(h.TimerStops, Does.Contain(LapbTimer.T1));
    }

    [Test]
    public void Connect_T1RetriesThenExhausts()
    {
        var h = new LapbTestHarness(new LapbConfig { N2 = 2, T3 = TimeSpan.Zero });
        h.Machine.Connect();
        h.TransmitAll();

        h.Machine.OnTimerExpired(LapbTimer.T1); // retry 1
        h.TransmitAll();
        h.Machine.OnTimerExpired(LapbTimer.T1); // retry 2
        h.TransmitAll();
        Assert.That(h.SentOfType(Ax25FrameType.SABM).Count(), Is.EqualTo(3));
        Assert.That(h.Machine.State, Is.EqualTo(LapbState.AwaitingConnection));

        h.Machine.OnTimerExpired(LapbTimer.T1); // exceeds N2
        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Disconnected));
        Assert.That(h.Transitions[^1].Reason, Is.EqualTo(LapbDisconnectReason.RetryExhausted));
    }

    [Test]
    public void Sabme_UaEntersExtendedMode()
    {
        var h = new LapbTestHarness(new LapbConfig { RequestExtended = true, T3 = TimeSpan.Zero });
        h.Machine.Connect();

        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.SABME));

        h.TransmitAll();
        h.Receive(Ax25FrameType.UA, pf: true);

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Connected));
        Assert.That(h.Machine.ExtendedMode, Is.True);
        Assert.That(h.Machine.Modulus, Is.EqualTo(128));
    }

    [TestCase(Ax25FrameType.DM)]
    [TestCase(Ax25FrameType.FRMR)]
    public void Sabme_RefusalFallsBackToSabmOnce(Ax25FrameType refusal)
    {
        var h = new LapbTestHarness(new LapbConfig { RequestExtended = true, T3 = TimeSpan.Zero });
        h.Machine.Connect();
        h.TransmitAll();
        h.Receive(refusal, pf: true);

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.AwaitingConnection), "fallback, not give-up");
        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.SABM));

        h.TransmitAll();
        h.Receive(Ax25FrameType.UA, pf: true);
        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Connected));
        Assert.That(h.Machine.ExtendedMode, Is.False, "downgraded to modulo-8");

        // A second refusal after the fallback is terminal.
        var h2 = new LapbTestHarness(new LapbConfig { RequestExtended = true, T3 = TimeSpan.Zero });
        h2.Machine.Connect();
        h2.TransmitAll();
        h2.Receive(Ax25FrameType.DM, pf: true);
        h2.Receive(Ax25FrameType.DM, pf: true);
        Assert.That(h2.Machine.State, Is.EqualTo(LapbState.Disconnected));
        Assert.That(h2.Transitions[^1].Reason, Is.EqualTo(LapbDisconnectReason.RemoteDm));
    }

    [Test]
    public void Sabm_DmDisconnects()
    {
        var h = new LapbTestHarness();
        h.Machine.Connect();
        h.TransmitAll();
        h.Receive(Ax25FrameType.DM, pf: true);

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Disconnected));
        Assert.That(h.Transitions[^1].Reason, Is.EqualTo(LapbDisconnectReason.RemoteDm));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AcceptIncoming_AnswersUaEchoingPoll(bool extended)
    {
        var h = new LapbTestHarness();
        h.Machine.AcceptIncoming(new LapbRxFrame(
            extended ? Ax25FrameType.SABME : Ax25FrameType.SABM,
            0, 0, PollFinal: true, IsCommand: true, null, ReadOnlyMemory<byte>.Empty));

        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Connected));
        Assert.That(h.Machine.ExtendedMode, Is.EqualTo(extended));
        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.UA));
        Assert.That(h.LastSent.PollFinal, Is.True, "UA F bit must echo the received P bit");
        Assert.That(h.LastSent.IsCommand, Is.False);
    }

    [Test]
    public void AcceptIncoming_IgnoredWhenNotDisconnected()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();

        h.Machine.AcceptIncoming(new LapbRxFrame(
            Ax25FrameType.SABM, 0, 0, true, true, null, ReadOnlyMemory<byte>.Empty));

        Assert.That(h.Sent, Is.Empty, "AcceptIncoming only applies in Disconnected");
    }

    [Test]
    public void SabmWhileConnected_ResetsTheLink()
    {
        var h = new LapbTestHarness();
        h.EstablishOutbound();

        // Put some sequence state on the link first.
        h.Machine.SendData("abc"u8);
        h.TransmitAll();
        Assert.That(h.Machine.Vs, Is.EqualTo(1));
        h.ClearRecordings();

        h.Receive(Ax25FrameType.SABM, pf: true, command: true);

        Assert.That(h.LastSent.Type, Is.EqualTo(Ax25FrameType.UA));
        Assert.That(h.Machine.State, Is.EqualTo(LapbState.Connected));
        Assert.That(h.Machine.Vs, Is.Zero, "link reset clears sequence state");
        Assert.That(h.Transitions, Does.Contain(
            (LapbState.Connected, LapbState.Connected, (LapbDisconnectReason?)LapbDisconnectReason.ProtocolError)));
    }
}
