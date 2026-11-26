using System.Net;
using System.Net.Sockets;
using DireControl.Api.Services;
using DireControl.Modem.Audio;
using DireControl.Modem.Ptt;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// One keyup's ordering and failure handling: the device opens before PTT
/// keys, a stalled playback is cut short by the watchdog, and an unkey is
/// retried before the session is declared stuck.
/// </summary>
[TestFixture]
public sealed class KeyupRunnerTests
{
    private sealed class FakePtt : IPttController
    {
        private readonly Lock _lock = new();
        private readonly List<bool> _history = [];
        public int FailUnkeys { get; set; }
        public volatile bool Keyed;

        public List<bool> History
        {
            get { lock (_lock) return [.. _history]; }
        }

        public void SetPtt(bool transmit)
        {
            lock (_lock)
            {
                if (!transmit && FailUnkeys > 0)
                {
                    FailUnkeys--;
                    throw new IOException("PTT line stuck");
                }
                _history.Add(transmit);
                Keyed = transmit;
            }
        }

        public void Dispose() { }
    }

    private sealed class FakeSink(ManualResetEventSlim? drainGate = null) : IPlaybackSink
    {
        public int Written;
        public void Write(ReadOnlySpan<float> samples) => Written += samples.Length;
        public void Drain() => drainGate?.Wait(TimeSpan.FromSeconds(10));
        public void Dispose() { }
    }

    private static readonly float[] Audio = new float[4800];

    private static KeyupResult Run(IPttController ptt, Func<IPlaybackSink> open, TimeSpan? limit = null) =>
        KeyupRunner.Run(ptt, open, Audio, limit ?? TimeSpan.FromSeconds(5), NullLogger.Instance, "N0CALL-10");

    [Test]
    public async Task BlockingOpen_NeverKeysWhileOpenIsPending()
    {
        var ptt = new FakePtt();
        using var openGate = new ManualResetEventSlim();

        var run = Task.Run(() => Run(ptt, () =>
        {
            openGate.Wait(TimeSpan.FromSeconds(10));
            return new FakeSink();
        }));

        await Task.Delay(200);
        Assert.That(ptt.History, Is.Empty, "PTT must not key before the device is open");

        openGate.Set();
        var result = await run;

        Assert.That(result.Outcome, Is.EqualTo(KeyupOutcome.Transmitted));
        Assert.That(ptt.History, Is.EqualTo(new[] { true, false }).AsCollection);
    }

    [Test]
    public void OpenFailure_NeverKeys()
    {
        var ptt = new FakePtt();

        var result = Run(ptt, () => throw new IOException("snd_pcm_open(\"hw:1,0\") failed: Device or resource busy"));

        Assert.That(result.Outcome, Is.EqualTo(KeyupOutcome.OpenFailed));
        Assert.That(ptt.History, Is.Empty);
    }

    [Test]
    public async Task StalledPlayback_WatchdogUnkeysWithinBound()
    {
        var ptt = new FakePtt();
        using var drainGate = new ManualResetEventSlim();

        var run = Task.Run(() => Run(ptt, () => new FakeSink(drainGate), TimeSpan.FromMilliseconds(300)));

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!ptt.Keyed && DateTime.UtcNow < deadline)
            await Task.Delay(5);
        Assert.That(ptt.Keyed, Is.True, "keyed once the device opened");

        var keyedAt = DateTime.UtcNow;
        while (ptt.Keyed && DateTime.UtcNow < deadline)
            await Task.Delay(5);
        var keyedFor = DateTime.UtcNow - keyedAt;

        Assert.That(ptt.Keyed, Is.False, "watchdog must unkey while drain is still blocked");
        Assert.That(keyedFor, Is.LessThan(TimeSpan.FromSeconds(1.5)));
        Assert.That(run.IsCompleted, Is.False, "the drain itself is still stuck");

        drainGate.Set();
        var result = await run;
        Assert.That(result.WatchdogTripped, Is.True);
    }

    [Test]
    public void UnkeyFailure_IsRetriedBeforeGivingUp()
    {
        var ptt = new FakePtt { FailUnkeys = 2 };

        var result = Run(ptt, () => new FakeSink());

        Assert.That(result.Outcome, Is.EqualTo(KeyupOutcome.Transmitted));
        Assert.That(ptt.History.Last(), Is.False);
    }

    [Test]
    public void UnkeyFailingEveryRetry_ReportsUnkeyFailed()
    {
        var ptt = new FakePtt { FailUnkeys = 3 };

        var result = Run(ptt, () => new FakeSink());

        Assert.That(result.Outcome, Is.EqualTo(KeyupOutcome.UnkeyFailed));
        Assert.That(ptt.Keyed, Is.True);
    }

    [TestCase(1.0, 3.0)]
    [TestCase(20.0, 24.0)]
    [TestCase(40.0, 30.0)]
    public void WatchdogLimit_AddsSlackAndCaps(double audioSeconds, double expectedSeconds)
    {
        Assert.That(KeyupRunner.WatchdogLimit(TimeSpan.FromSeconds(audioSeconds)).TotalSeconds,
            Is.EqualTo(expectedSeconds).Within(0.001));
    }

    [Test]
    public async Task RigctldDispose_ReconnectsToUnkeyAfterServerDropped()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var secondConnection = new List<string>();

        var server = Task.Run(async () =>
        {
            // First connection: acknowledge T 1, then hang up mid-keyup.
            using (var first = await listener.AcceptTcpClientAsync())
            {
                var reader = new StreamReader(first.GetStream());
                var writer = new StreamWriter(first.GetStream()) { AutoFlush = true, NewLine = "\n" };
                Assert.That(await reader.ReadLineAsync(), Is.EqualTo("T 1"));
                await writer.WriteLineAsync("RPRT 0");
            }

            // Second connection: whatever Dispose sends.
            using var second = await listener.AcceptTcpClientAsync();
            var r2 = new StreamReader(second.GetStream());
            var w2 = new StreamWriter(second.GetStream()) { AutoFlush = true, NewLine = "\n" };
            while (await r2.ReadLineAsync() is { } line)
            {
                lock (secondConnection) secondConnection.Add(line);
                await w2.WriteLineAsync("RPRT 0");
            }
        });

        var ptt = new RigctldPtt("127.0.0.1", port);
        ptt.SetPtt(true);
        await Task.Delay(100); // let the server close the first connection
        ptt.Dispose();

        await Task.WhenAny(server, Task.Delay(3000));
        lock (secondConnection)
            Assert.That(secondConnection, Is.EqualTo(new[] { "T 0" }).AsCollection);
        listener.Stop();
    }
}
