using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DireControl.Api.Services;
using DireControl.Modem;
using DireControl.Modem.Ax25;
using DireControl.Modem.Dsp;
using DireControl.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// The capture thread hands decoded frames to <see cref="RfIngestDispatcher"/>
/// and moves on; a slow database or a stuck network client must never stall
/// the demodulator.
/// </summary>
[TestFixture]
public sealed class RfIngestDispatcherTests
{
    private const int SampleRate = 48000;

    [Test]
    public async Task BlockingIngest_DoesNotStallProcessSamples()
    {
        var dispatcher = new RfIngestDispatcher();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ingested = new List<byte[]>();
        using var cts = new CancellationTokenSource();

        // An ingest that blocks for 2 s per frame, synchronously — exactly how
        // a locked SQLite SaveChangesAsync behaves.
        var runner = dispatcher.RunAsync((item, _) =>
        {
            release.Task.Wait(TimeSpan.FromSeconds(2));
            lock (ingested) ingested.Add(item.Frame);
            return Task.CompletedTask;
        }, NullLogger.Instance, cts.Token);

        var receiver = AfskReceiver.CreateStandard(SampleRate);
        receiver.FrameReceived += (frame, _) => dispatcher.Post(new RfIngestItem(frame, 0, null));

        var modulator = new AfskModulator(SampleRate);
        var audio = new List<float>();
        for (var i = 0; i < 3; i++)
        {
            audio.AddRange(modulator.GenerateFrame(Ax25Encoder.EncodeUiFrame($"N4WR-{i + 1}", $"frame {i}", null)));
            audio.AddRange(new float[SampleRate / 10]);
        }
        var samples = audio.ToArray();

        // Warm the JIT so timing measures the hand-off, not first-call compilation.
        AfskReceiver.CreateStandard(SampleRate).ProcessSamples(samples.AsSpan(0, 4096));

        var worst = TimeSpan.Zero;
        for (var pos = 0; pos < samples.Length; pos += 1024)
        {
            var sw = Stopwatch.StartNew();
            receiver.ProcessSamples(samples.AsSpan(pos, Math.Min(1024, samples.Length - pos)));
            if (sw.Elapsed > worst)
                worst = sw.Elapsed;
        }

        Assert.That(receiver.ValidFrameCount, Is.EqualTo(3));
        Assert.That(worst, Is.LessThan(TimeSpan.FromMilliseconds(50)),
            "a decode must hand off to ingest without waiting for it");

        release.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (ingested)
                if (ingested.Count == 3)
                    break;
            await Task.Delay(10);
        }
        await cts.CancelAsync();
        Assert.That(async () => await runner, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(ingested, Has.Count.EqualTo(3), "every frame is still ingested, in order");
    }

    [Test]
    public void Overflow_DropsOldestAndCounts()
    {
        var dispatcher = new RfIngestDispatcher(capacity: 4);

        for (var i = 0; i < 10; i++)
            dispatcher.Post(new RfIngestItem([(byte)i], 0, null));

        Assert.That(dispatcher.Dropped, Is.EqualTo(6));
    }

    [Test]
    public async Task FailingIngest_DoesNotStopTheLoop()
    {
        var dispatcher = new RfIngestDispatcher();
        var seen = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var runner = dispatcher.RunAsync((item, _) =>
        {
            if (Interlocked.Increment(ref seen) == 2)
                done.TrySetResult();
            return item.Frame[0] == 0
                ? Task.FromException(new InvalidOperationException("database is locked"))
                : Task.CompletedTask;
        }, NullLogger.Instance, cts.Token);

        dispatcher.Post(new RfIngestItem([0], 0, null));
        dispatcher.Post(new RfIngestItem([1], 0, null));

        Assert.That(await Task.WhenAny(done.Task, Task.Delay(5000)), Is.SameAs(done.Task));
        await cts.CancelAsync();
        Assert.That(async () => await runner, Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task KissBroadcast_ToClientThatNeverReads_ReturnsPromptly()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var harness = new IngestHarness(s =>
        {
            s.KissServerEnabled = true;
            s.KissServerPort = port;
            s.KissServerBindAddress = "127.0.0.1";
        });
        var server = new KissTcpServerService(
            harness.ScopeFactory, harness.Transmitter, new RestartSignal(),
            NullLogger<KissTcpServerService>.Instance);
        await server.StartAsync(CancellationToken.None);
        try
        {
            using var client = new TcpClient();
            for (var attempt = 0; ; attempt++)
            {
                try { await client.ConnectAsync(IPAddress.Loopback, port); break; }
                catch (SocketException) when (attempt < 50) { await Task.Delay(50); }
            }
            while (server.ClientCount == 0)
                await Task.Delay(10);

            // Far more than the kernel's socket buffers hold: the old
            // synchronous Write blocked here indefinitely.
            var frame = Ax25Encoder.EncodeUiFrame("N4WR-1", new string('x', 250), "WIDE1-1");
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 40_000; i++)
                server.Broadcast(frame, 0);

            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }
}
