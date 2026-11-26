using DireControl.Api.Services;
using DireControl.Data.Models;
using DireControl.Enums;
using DireControl.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// The parser drains a backlog in one pass instead of sleeping 5 s after
/// every 50-row batch.
/// </summary>
[TestFixture]
public sealed class PacketParserDrainTests
{
    [Test]
    public async Task Backlog_DrainsInOnePass()
    {
        using var harness = new IngestHarness();
        await using (var db = harness.CreateDb())
        {
            for (var i = 0; i < 4; i++)
                db.Stations.Add(new Station
                {
                    Callsign = $"K4ABC-{i + 1}", FirstSeen = DateTime.UtcNow, LastSeen = DateTime.UtcNow, Symbol = "/-",
                });
            for (var i = 0; i < 120; i++)
                db.Packets.Add(new Packet
                {
                    StationCallsign = $"K4ABC-{i % 4 + 1}",
                    RawPacket = $"K4ABC-{i % 4 + 1}>APRS,WIDE1-1:>status {i}",
                    ReceivedAt = DateTime.UtcNow.AddSeconds(-120 + i),
                    Source = PacketSource.Rf,
                });
            await db.SaveChangesAsync();
        }

        var options = Options.Create(new DireControlOptions { OurCallsign = IngestHarness.OurCallsign });
        var service = new AprsPacketParsingService(
            harness.ScopeFactory,
            new NullHubContext(),
            options,
            new MessageSendingService(
                harness.Transmitter, harness.AprsIsTxQueue, harness.ScopeFactory, options,
                NullLogger<MessageSendingService>.Instance),
            new PendingAlertChannel(),
            NullLogger<AprsPacketParsingService>.Instance);

        var taken = await service.DrainAsync(default);

        Assert.That(taken, Is.EqualTo(120));
        await using var check = harness.CreateDb();
        Assert.That(await check.Packets.CountAsync(p => p.ParsedType == PacketType.Unknown), Is.Zero);
    }

    private sealed class ThrowingHubContext : IHubContext<DireControl.Api.Hubs.PacketHub>
    {
        public IHubClients Clients => throw new InvalidOperationException("hub down");
        public IGroupManager Groups => throw new InvalidOperationException("hub down");
    }

    [Test]
    public async Task PostSaveFailure_LeavesThePacketParsed()
    {
        using var harness = new IngestHarness();
        await using (var db = harness.CreateDb())
        {
            db.Stations.Add(new Station
            {
                Callsign = "K4ABC-1", FirstSeen = DateTime.UtcNow, LastSeen = DateTime.UtcNow, Symbol = "/-",
            });
            db.Packets.Add(new Packet
            {
                StationCallsign = "K4ABC-1",
                RawPacket = "K4ABC-1>APRS,WIDE1-1:>status",
                ReceivedAt = DateTime.UtcNow,
                Source = PacketSource.Rf,
            });
            await db.SaveChangesAsync();
        }

        var options = Options.Create(new DireControlOptions { OurCallsign = IngestHarness.OurCallsign });
        var service = new AprsPacketParsingService(
            harness.ScopeFactory,
            new ThrowingHubContext(),
            options,
            new MessageSendingService(
                harness.Transmitter, harness.AprsIsTxQueue, harness.ScopeFactory, options,
                NullLogger<MessageSendingService>.Instance),
            new PendingAlertChannel(),
            NullLogger<AprsPacketParsingService>.Instance);

        await service.DrainAsync(default);

        await using var check = harness.CreateDb();
        var parsed = await check.Packets.SingleAsync();
        Assert.That(parsed.ParsedType, Is.Not.EqualTo(PacketType.Unparseable),
            "a broadcast failure after the save must not mark a good packet unparseable");
        Assert.That(parsed.ParsedType, Is.Not.EqualTo(PacketType.Unknown));
    }
}
