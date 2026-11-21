using System.Net;
using System.Net.Sockets;
using DireControl.Api.Services;
using DireControl.Data.Models;
using DireControl.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// The send-only APRS-IS connection against a fake server: lines the server
/// pushes reach the receive pipeline, and a silent server is dropped.  Plus
/// the receive path's tolerance of a failing database.
/// </summary>
[TestFixture]
public sealed class AprsIsConnectionTests
{
    private static (TcpListener Listener, int Port) Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    private static AprsIsTxService CreateTx(IngestHarness harness, AprsIsTxInbound inbound) =>
        new(harness.ScopeFactory, harness.AprsIsTxQueue, inbound, new RestartSignal(),
            Options.Create(new DireControlOptions { OurCallsign = IngestHarness.OurCallsign }),
            NullLogger<AprsIsTxService>.Instance);

    [Test]
    public async Task TxConnection_ServerPushedLines_ReachTheReceivePipeline()
    {
        var (listener, port) = Listen();
        using var harness = new IngestHarness(s => { s.AprsIsHost = "127.0.0.1"; s.AprsIsPort = port; });
        var inbound = new AprsIsTxInbound();
        var tx = CreateTx(harness, inbound);

        harness.AprsIsTxQueue.Enqueue("K4ABC>APRS,qAR,N0CALL-10:>gated status");
        await tx.StartAsync(CancellationToken.None);
        try
        {
            using var conn = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var reader = new StreamReader(conn.GetStream());
            var writer = new StreamWriter(conn.GetStream()) { AutoFlush = true, NewLine = "\r\n" };
            Assert.That(await reader.ReadLineAsync(), Does.StartWith("user N0CALL-10 pass "));
            await writer.WriteLineAsync("# logresp N0CALL-10 verified, server T2TEST");
            await writer.WriteLineAsync("W1AW>APRS,TCPIP*,qAC,T2TEST::K4ABC    :hello there{7");

            var line = await inbound.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(line, Is.EqualTo("W1AW>APRS,TCPIP*,qAC,T2TEST::K4ABC    :hello there{7"));
        }
        finally
        {
            await tx.StopAsync(CancellationToken.None);
            listener.Stop();
        }
    }

    [Test]
    public async Task TxConnection_SilentServer_IsDroppedAfterInactivityTimeout()
    {
        var (listener, port) = Listen();
        using var harness = new IngestHarness(s => { s.AprsIsHost = "127.0.0.1"; s.AprsIsPort = port; });
        var tx = CreateTx(harness, new AprsIsTxInbound());
        tx.InactivityTimeout = TimeSpan.FromMilliseconds(400);
        tx.RetryDelay = TimeSpan.FromMilliseconds(50);

        harness.AprsIsTxQueue.Enqueue("K4ABC>APRS,qAR,N0CALL-10:>one");
        await tx.StartAsync(CancellationToken.None);
        try
        {
            using var conn = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var reader = new StreamReader(conn.GetStream());
            await reader.ReadLineAsync(); // login
            await reader.ReadLineAsync(); // the queued line

            // Never answer.  The client must hang up on its own.
            var eof = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(eof, Is.Null, "client closed the half-open connection");

            harness.AprsIsTxQueue.Enqueue("K4ABC>APRS,qAR,N0CALL-10:>two");
            using var second = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(second.Connected, Is.True, "and reconnected for the next line");
        }
        finally
        {
            await tx.StopAsync(CancellationToken.None);
            listener.Stop();
        }
    }

    [Test]
    public async Task ReceivePath_DatabaseFailure_DoesNotPropagate()
    {
        using var harness = new IngestHarness();
        await using (var db = harness.CreateDb())
            await db.Database.ExecuteSqlRawAsync("DROP TABLE Packets");

        var service = new AprsIsService(
            harness.ScopeFactory,
            new AprsIsStatusService(NullLogger<AprsIsStatusService>.Instance),
            new RestartSignal(),
            harness.Transmitter,
            new AprsIsTxInbound(),
            Options.Create(new DireControlOptions { OurCallsign = IngestHarness.OurCallsign }),
            NullLogger<AprsIsService>.Instance);

        Assert.That(
            async () => await service.ProcessLineSafelyAsync(
                "W1AW>APRS,TCPIP*,qAC,T2TEST:>status", new UserSetting { Id = 1 }, default),
            Throws.Nothing);
    }
}
