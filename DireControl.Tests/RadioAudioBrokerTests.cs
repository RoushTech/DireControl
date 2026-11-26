using DireControl.Api.Services;
using DireControl.Modem.Audio;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// The audio broker sits between the realtime DSP thread and any number of
/// listening browsers.  It must never block the modem, must route frames only
/// to the radio and options each client asked for, and must drop rather than
/// queue without bound when a client stops keeping up.
/// </summary>
[TestFixture]
public sealed class RadioAudioBrokerTests
{
    private static byte[] Frame(byte marker)
    {
        var frame = new byte[AudioMonitorResampler.FrameBytes];
        Array.Fill(frame, marker);
        return frame;
    }

    /// <summary>
    /// Begins a stream and registers the listener.  Registration happens
    /// synchronously inside the first MoveNextAsync, so the returned pending
    /// read is already subscribed when this returns.
    /// </summary>
    private static (IAsyncEnumerator<byte[]> Enumerator, ValueTask<bool> Pending) Listen(
        RadioAudioBroker broker,
        string radioId,
        CancellationToken ct,
        bool squelchGated = false,
        bool includeTx = false)
    {
        var enumerator = broker.ListenAsync(radioId, squelchGated, includeTx, ct).GetAsyncEnumerator();
        return (enumerator, enumerator.MoveNextAsync());
    }

    private static CancellationTokenSource Timeout() => new(TimeSpan.FromSeconds(5));

    [Test]
    public void PublishWithNoListeners_IsAHarmlessNoOp()
    {
        var broker = new RadioAudioBroker();

        Assert.That(broker.HasListeners("radio-1"), Is.False);
        Assert.That(broker.HasTxListeners("radio-1"), Is.False);
        Assert.DoesNotThrow(() => broker.PublishReceive("radio-1", Frame(1), carrierDetected: true));
        Assert.DoesNotThrow(() => broker.PublishTransmit("radio-1", Frame(1)));
    }

    [Test]
    public async Task Listener_ReceivesPublishedReceiveAudio()
    {
        var broker = new RadioAudioBroker();
        using var cts = Timeout();
        var (enumerator, pending) = Listen(broker, "radio-1", cts.Token);

        Assert.That(broker.HasListeners("radio-1"), Is.True);

        var frame = Frame(7);
        broker.PublishReceive("radio-1", frame, carrierDetected: true);

        Assert.That(await pending, Is.True);
        Assert.That(enumerator.Current, Is.EqualTo(frame));

        await cts.CancelAsync();
    }

    [Test]
    public async Task Listener_OnlyReceivesItsOwnRadio()
    {
        var broker = new RadioAudioBroker();
        using var cts = Timeout();
        var (enumerator, pending) = Listen(broker, "radio-1", cts.Token);

        // A second radio's audio must not leak into this stream.
        broker.PublishReceive("radio-2", Frame(2), carrierDetected: true);
        await Task.Delay(50, TestContext.CurrentContext.CancellationToken);
        Assert.That(pending.IsCompleted, Is.False, "audio from another radio was delivered");

        var mine = Frame(1);
        broker.PublishReceive("radio-1", mine, carrierDetected: true);
        Assert.That(await pending, Is.True);
        Assert.That(enumerator.Current, Is.EqualTo(mine));

        await cts.CancelAsync();
    }

    [Test]
    public async Task SquelchGate_BlocksAudioUntilCarrierAppears()
    {
        var broker = new RadioAudioBroker();
        using var cts = Timeout();
        var (enumerator, pending) = Listen(broker, "radio-1", cts.Token, squelchGated: true);

        broker.PublishReceive("radio-1", Frame(1), carrierDetected: false);
        await Task.Delay(50, TestContext.CurrentContext.CancellationToken);
        Assert.That(pending.IsCompleted, Is.False, "gated stream delivered audio with no carrier");

        var withCarrier = Frame(9);
        broker.PublishReceive("radio-1", withCarrier, carrierDetected: true);
        Assert.That(await pending, Is.True);
        Assert.That(enumerator.Current, Is.EqualTo(withCarrier));

        await cts.CancelAsync();
    }

    [Test]
    public async Task SquelchGate_StaysOpenForTheHangPeriodAfterCarrierDrops()
    {
        var broker = new RadioAudioBroker();
        using var cts = Timeout();
        var (enumerator, pending) = Listen(broker, "radio-1", cts.Token, squelchGated: true);

        broker.PublishReceive("radio-1", Frame(1), carrierDetected: true);
        Assert.That(await pending, Is.True);

        // Carrier has dropped but we are still inside the hang window, so the
        // tail of the transmission must not be chopped off.
        var tail = Frame(2);
        broker.PublishReceive("radio-1", tail, carrierDetected: false);

        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        Assert.That(enumerator.Current, Is.EqualTo(tail));

        await cts.CancelAsync();
    }

    [Test]
    public async Task UngatedListener_HearsRawAudioWithNoCarrier()
    {
        var broker = new RadioAudioBroker();
        using var cts = Timeout();
        var (enumerator, pending) = Listen(broker, "radio-1", cts.Token, squelchGated: false);

        // Hearing an open, noisy channel is the point of the ungated mode.
        var noise = Frame(3);
        broker.PublishReceive("radio-1", noise, carrierDetected: false);

        Assert.That(await pending, Is.True);
        Assert.That(enumerator.Current, Is.EqualTo(noise));

        await cts.CancelAsync();
    }

    [Test]
    public async Task TransmitAudio_ReachesOnlyListenersThatOptedIn()
    {
        var broker = new RadioAudioBroker();
        using var cts = Timeout();
        var (_, rxOnly) = Listen(broker, "radio-1", cts.Token, includeTx: false);
        var (monitorEnumerator, monitoring) = Listen(broker, "radio-1", cts.Token, includeTx: true);

        Assert.That(broker.HasTxListeners("radio-1"), Is.True);

        var txFrame = Frame(5);
        broker.PublishTransmit("radio-1", txFrame);

        Assert.That(await monitoring, Is.True);
        Assert.That(monitorEnumerator.Current, Is.EqualTo(txFrame));
        Assert.That(rxOnly.IsCompleted, Is.False, "TX audio leaked to a receive-only listener");

        // Release the receive-only stream so it is not left mid-read.
        broker.PublishReceive("radio-1", Frame(6), carrierDetected: true);
        Assert.That(await rxOnly, Is.True);

        await cts.CancelAsync();
    }

    [Test]
    public async Task TransmitAudio_IgnoresTheSquelchGate()
    {
        var broker = new RadioAudioBroker();
        using var cts = Timeout();
        var (enumerator, pending) = Listen(broker, "radio-1", cts.Token, squelchGated: true, includeTx: true);

        // Our own keyup has no carrier to detect — gating it would mean never
        // hearing our own transmissions.
        var txFrame = Frame(4);
        broker.PublishTransmit("radio-1", txFrame);

        Assert.That(await pending, Is.True);
        Assert.That(enumerator.Current, Is.EqualTo(txFrame));

        await cts.CancelAsync();
    }

    [Test]
    public async Task SlowListener_DropsOldestFramesInsteadOfGrowingWithoutBound()
    {
        var broker = new RadioAudioBroker();
        using var cts = Timeout();
        var (enumerator, pending) = Listen(broker, "radio-1", cts.Token);

        // The first frame satisfies the pending read; the rest pile into a
        // bounded queue, so far more than it holds must not block the publisher.
        for (var i = 0; i < 500; i++)
            Assert.DoesNotThrow(() => broker.PublishReceive("radio-1", Frame(1), carrierDetected: true));

        Assert.That(await pending, Is.True);

        var buffered = 0;
        while (enumerator.MoveNextAsync() is { IsCompleted: true } next && next.Result)
            buffered++;

        // Bounded at 64 frames of backlog, not the 499 that were published.
        Assert.That(buffered, Is.LessThanOrEqualTo(64));

        await cts.CancelAsync();
    }

    [Test]
    public async Task EndingAStream_UnregistersTheListener()
    {
        var broker = new RadioAudioBroker();
        using var cts = Timeout();
        var (enumerator, pending) = Listen(broker, "radio-1", cts.Token);

        broker.PublishReceive("radio-1", Frame(1), carrierDetected: true);
        Assert.That(await pending, Is.True);
        Assert.That(broker.HasListeners("radio-1"), Is.True);

        await enumerator.DisposeAsync();

        // A departed client must stop costing the DSP thread anything.
        Assert.That(broker.HasListeners("radio-1"), Is.False);
    }
}
