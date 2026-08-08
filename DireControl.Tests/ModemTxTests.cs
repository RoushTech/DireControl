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
