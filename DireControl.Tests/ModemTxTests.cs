using System.Net;
using System.Net.Sockets;
using DireControl.Modem;
using DireControl.Modem.Ax25;
using DireControl.Modem.Dsp;
using DireControl.Modem.Ptt;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Transmit-path tests: CM108 HID report layout, rigctld PTT against a fake
/// server, TXDelay flag math, and multi-frame single-keyup transmissions
/// decoding back through the receive pipeline.
/// </summary>
[TestFixture]
public sealed class ModemTxTests
{
    private const int SampleRate = 48000;

    // ── CM108 ────────────────────────────────────────────────────────────────

    [Test]
    public void Cm108Report_Pin3()
    {
        // [report id, HID register, GPIO data, GPIO direction]
        Assert.That(Cm108Ptt.BuildPttReport(3, transmit: true), Is.EqualTo(new byte[] { 0x00, 0x00, 0x04, 0x04 }).AsCollection);
        Assert.That(Cm108Ptt.BuildPttReport(3, transmit: false), Is.EqualTo(new byte[] { 0x00, 0x00, 0x00, 0x04 }).AsCollection);
    }

    [Test]
    public void Cm108Report_AllPins_SetOnlyTheirBit()
    {
        for (var pin = 1; pin <= 8; pin++)
        {
            var report = Cm108Ptt.BuildPttReport(pin, transmit: true);
            Assert.That(report[2], Is.EqualTo(1 << (pin - 1)), $"pin {pin} data");
            Assert.That(report[3], Is.EqualTo(1 << (pin - 1)), $"pin {pin} direction");
        }
    }

    // ── rigctld ──────────────────────────────────────────────────────────────

    [Test]
    public async Task RigctldPtt_KeysAndReadsFrequency()
    {
        var commands = new List<string>();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Minimal rigctld: replies RPRT 0 to T commands, a frequency to f.
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(client.GetStream());
            await using var writer = new StreamWriter(client.GetStream()) { AutoFlush = true, NewLine = "\n" };
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (commands)
                    commands.Add(line);
                await writer.WriteLineAsync(line == "f" ? "144390000" : "RPRT 0");
            }
        });

        using (var ptt = new RigctldPtt("127.0.0.1", port))
        {
            ptt.SetPtt(true);
            ptt.SetPtt(false);
            Assert.That(ptt.TryGetFrequencyHz(), Is.EqualTo(144_390_000L));
        }

        listener.Stop();
        try { await serverTask; } catch (SocketException) { /* listener stopped */ }

        // Dispose sends a defensive final unkey.
        lock (commands)
            Assert.That(commands, Is.EqualTo(new[] { "T 1", "T 0", "f", "T 0" }).AsCollection);
    }

    [Test]
    public void RigctldPtt_UnreachableServer_ThrowsOnKeyButNotOnFrequency()
    {
        // Port 1 on localhost is essentially guaranteed closed.
        using var ptt = new RigctldPtt("127.0.0.1", 1);
        Assert.That(ptt.TryGetFrequencyHz(), Is.Null);
        Assert.Throws<IOException>(() => ptt.SetPtt(true));
    }

    // ── Modulator TX timing ──────────────────────────────────────────────────

    [Test]
    public void FlagsForMilliseconds_MatchesAirTime()
    {
        var modulator = new AfskModulator(SampleRate);

        // One flag is 8 bits at 1200 bd = 6.67 ms.
        Assert.That(modulator.FlagsForMilliseconds(300), Is.EqualTo(45));
        Assert.That(modulator.FlagsForMilliseconds(50), Is.EqualTo(8));
        Assert.That(modulator.FlagsForMilliseconds(0), Is.EqualTo(1), "always at least one opening flag");
    }

    [Test]
    public void GenerateTransmission_MultipleFramesOneKeyup_AllDecode()
    {
        var modulator = new AfskModulator(SampleRate);
        var frames = Enumerable.Range(0, 3)
            .Select(i => Ax25Encoder.EncodeUiFrame($"N4WR-{i + 1}", $">multi-frame keyup test {i}", "WIDE1-1"))
            .ToList();

        var audio = modulator.GenerateTransmission(frames, leadFlags: 45, tailFlags: 8, amplitude: 0.8f);

        var receiver = AfskReceiver.CreateStandard(SampleRate);
        var decoded = new List<byte[]>();
        receiver.FrameReceived += (frame, _) => decoded.Add(frame);
        receiver.ProcessSamples(audio);
        receiver.ProcessSamples(new float[SampleRate / 10]);

        Assert.That(decoded, Has.Count.EqualTo(frames.Count));
        for (var i = 0; i < frames.Count; i++)
            Assert.That(decoded[i], Is.EqualTo(frames[i]).AsCollection, $"frame {i}");
    }

    // ── Keyup drain policy ───────────────────────────────────────────────────

    private static System.Threading.Channels.Channel<DireControl.Api.Services.TxItem> MakeLane(int capacity = 64) =>
        System.Threading.Channels.Channel.CreateBounded<DireControl.Api.Services.TxItem>(capacity);

    [Test]
    public void DrainForKeyup_PriorityLaneGoesFirst_Fully()
    {
        var priority = MakeLane();
        var normal = MakeLane();
        normal.Writer.TryWrite(new DireControl.Api.Services.TxItem([1], null));
        priority.Writer.TryWrite(new DireControl.Api.Services.TxItem([2], null));
        priority.Writer.TryWrite(new DireControl.Api.Services.TxItem([3], null));

        var items = DireControl.Api.Services.SoundModemService.DrainForKeyup(
            priority.Reader, normal.Reader, maxNormal: 5);

        Assert.That(items.Select(i => i.Frame[0]), Is.EqualTo([2, 3, 1]).AsCollection,
            "session frames transmit ahead of the normal backlog");
    }

    [Test]
    public void DrainForKeyup_NormalLaneCapped_RemainderSurvives()
    {
        var priority = MakeLane();
        var normal = MakeLane();
        for (byte i = 0; i < 8; i++)
            normal.Writer.TryWrite(new DireControl.Api.Services.TxItem([i], null));

        var items = DireControl.Api.Services.SoundModemService.DrainForKeyup(
            priority.Reader, normal.Reader, maxNormal: 5);

        Assert.That(items, Has.Count.EqualTo(5), "keyup length is bounded");
        Assert.That(normal.Reader.Count, Is.EqualTo(3), "remainder waits for the next CSMA cycle");
        Assert.That(items.Select(i => i.Frame[0]), Is.EqualTo([0, 1, 2, 3, 4]).AsCollection, "FIFO preserved");
    }

    [Test]
    public void DrainForKeyup_PriorityNotSubjectToNormalCap()
    {
        var priority = MakeLane();
        var normal = MakeLane();
        for (byte i = 0; i < 7; i++)
            priority.Writer.TryWrite(new DireControl.Api.Services.TxItem([i], null));

        var items = DireControl.Api.Services.SoundModemService.DrainForKeyup(
            priority.Reader, normal.Reader, maxNormal: 5);

        Assert.That(items, Has.Count.EqualTo(7),
            "the session lane is already bounded by the LAPB window, not the keyup cap");
    }

    [Test]
    public void DrainForKeyup_AirtimeBudget_KeepsKeyupShortAndLeavesTheRestQueued()
    {
        var priority = MakeLane();
        var digipeat = MakeLane();
        var normal = MakeLane();
        for (var i = 0; i < 32; i++)
            priority.Writer.TryWrite(new DireControl.Api.Services.TxItem(
                Ax25Encoder.EncodeUiFrame("N4WR-1", new string('i', 128), "WIDE1-1"), null));
        for (var i = 0; i < 5; i++)
            normal.Writer.TryWrite(new DireControl.Api.Services.TxItem(
                Ax25Encoder.EncodeUiFrame("N4WR-1", new string('u', 256), "WIDE1-1"), null));

        var modulator = new AfskModulator(SampleRate);
        const int lead = 45, tail = 3;
        var budget = (long)(DireControl.Api.Services.SoundModemService.KeyupBudgetSeconds * 1200) - (lead + tail) * 8;

        var items = DireControl.Api.Services.SoundModemService.DrainForKeyup(
            priority.Reader, digipeat.Reader, normal.Reader, maxNormal: 5, budget);
        var audio = modulator.GenerateTransmission(items.Select(i => i.Frame).ToList(), lead, tail, 0.5f);

        Assert.That(audio.Length / (double)SampleRate,
            Is.LessThanOrEqualTo(DireControl.Api.Services.SoundModemService.KeyupBudgetSeconds));
        Assert.That(items, Is.Not.Empty);
        Assert.That(priority.Reader.Count + normal.Reader.Count, Is.EqualTo(37 - items.Count),
            "everything that did not fit stays queued, session lane included");
    }

    [Test]
    public void DrainForKeyup_OversizedFirstFrame_IsStillTaken()
    {
        var priority = MakeLane();
        var normal = MakeLane();
        normal.Writer.TryWrite(new DireControl.Api.Services.TxItem(
            Ax25Encoder.EncodeUiFrame("N4WR-1", new string('u', 256), null), null));

        var items = DireControl.Api.Services.SoundModemService.DrainForKeyup(
            priority.Reader, null, normal.Reader, maxNormal: 5, bitBudget: 10);

        Assert.That(items, Has.Count.EqualTo(1));
    }

    [Test]
    public void DrainForKeyup_DigipeatsGoBeforeBeacons_OnePerKeyup()
    {
        var priority = MakeLane();
        var digipeat = MakeLane();
        var normal = MakeLane();
        normal.Writer.TryWrite(new DireControl.Api.Services.TxItem([1], null));     // beacon
        digipeat.Writer.TryWrite(new DireControl.Api.Services.TxItem([20], null));  // digipeats
        digipeat.Writer.TryWrite(new DireControl.Api.Services.TxItem([21], null));

        var items = DireControl.Api.Services.SoundModemService.DrainForKeyup(
            priority.Reader, digipeat.Reader, normal.Reader, maxNormal: 5, long.MaxValue);

        Assert.That(items.Select(i => i.Frame[0]), Is.EqualTo([20, 1]).AsCollection);
        Assert.That(digipeat.Reader.Count, Is.EqualTo(1));
    }

    [Test]
    public void DroppingLane_CancelsTheDroppedFramesCompletion()
    {
        var dropped = new List<DireControl.Api.Services.TxItem>();
        var lane = DireControl.Api.Services.SoundModemService.CreateDroppingLane(2, dropped.Add);
        var first = new TaskCompletionSource<bool>();

        lane.Writer.TryWrite(new DireControl.Api.Services.TxItem([1], first));
        lane.Writer.TryWrite(new DireControl.Api.Services.TxItem([2], new TaskCompletionSource<bool>()));
        lane.Writer.TryWrite(new DireControl.Api.Services.TxItem([3], new TaskCompletionSource<bool>()));

        Assert.That(first.Task.IsCanceled, Is.True, "the producer learns its frame was never sent");
        Assert.That(dropped.Select(d => d.Frame[0]), Is.EqualTo([1]).AsCollection);
    }

    [Test]
    public void CancelPending_ClosesTheLaneSoLateFramesAreRefused()
    {
        var lane = MakeLane();
        var queued = new TaskCompletionSource<bool>();
        lane.Writer.TryWrite(new DireControl.Api.Services.TxItem([1], queued));

        DireControl.Api.Services.SoundModemService.CancelPending(lane);

        Assert.That(queued.Task.IsCanceled, Is.True);
        Assert.That(lane.Writer.TryWrite(new DireControl.Api.Services.TxItem([2], null)), Is.False,
            "a frame arriving after teardown is refused, not stranded");
    }

    [Test]
    public void GenerateTransmission_EndsWithARampNotAStep()
    {
        var frame = Ax25Encoder.EncodeUiFrame("N4WR", ">x", string.Empty);
        var audio = new AfskModulator(SampleRate).GenerateTransmission([frame], leadFlags: 30, tailFlags: 2, amplitude: 0.8f);

        Assert.That(MathF.Abs(audio[^1]), Is.LessThan(0.01f), "the final sample is faded out");
        Assert.That(audio.Max(MathF.Abs), Is.EqualTo(0.8f).Within(0.001f), "the body keeps full amplitude");
    }

    [Test]
    public void GenerateTransmission_TxDelayProducesExpectedAudioLength()
    {
        var modulator = new AfskModulator(SampleRate);
        var frame = Ax25Encoder.EncodeUiFrame("N4WR", ">x", string.Empty);

        var shortDelay = modulator.GenerateTransmission([frame], leadFlags: 1, tailFlags: 1, amplitude: 0.8f);
        var longDelay = modulator.GenerateTransmission([frame], leadFlags: 46, tailFlags: 1, amplitude: 0.8f);

        // 45 extra flags = 360 extra bits = 300 ms of air time.
        var extraSamples = longDelay.Length - shortDelay.Length;
        var expected = 45 * 8 * SampleRate / 1200;
        Assert.That(extraSamples, Is.EqualTo(expected).Within(SampleRate / 100), "TXDelay air time");
    }
}
