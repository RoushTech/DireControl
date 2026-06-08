using DireControl.Api.Hubs;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Tests for packet reprocessing: the selection query, and the end-to-end re-derivation
/// behaviour (repairs fields, stamps the parser version, and — critically — does NOT
/// replay side effects onto live Station state).
/// </summary>
[TestFixture]
public sealed class PacketReprocessingTests
{
    // =========================================================================
    // ReprocessOneAsync — end-to-end re-derivation. Guards the safety property:
    // reprocessing repairs the packet's own fields but must not mutate the live
    // Station record (positions, symbol, type) from a replayed historical packet.
    // =========================================================================

    [Test]
    public async Task ReprocessOneAsync_RepairsFields_StampsVersion_AndDoesNotMutateStation()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var dbOptions = new DbContextOptionsBuilder<DireControlContext>().UseSqlite(connection).Options;
        using var db = new DireControlContext(dbOptions);
        db.Database.EnsureCreated();

        // The real source station already has a known position from live operation.
        db.Stations.Add(new Station
        {
            Callsign = "K4TUX-10",
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
            LastLat = 10.0,
            LastLon = 20.0,
            Symbol = "/-",
            StationType = StationType.Fixed,
        });
        // A pre-fix row: corrupted StationCallsign, stale parser version, intact RawPacket.
        db.Stations.Add(new Station
        {
            Callsign = "GARBAGE",
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
            Symbol = "/-",
        });
        db.Packets.Add(new Packet
        {
            Id = 1,
            StationCallsign = "GARBAGE",
            RawPacket = "K4TUX-10>APRS,WIDE1-1:!3346.02N/08406.98W-Test",
            ReceivedAt = DateTime.UtcNow,
            Source = PacketSource.Rf,
            ParsedType = PacketType.Unknown,
            ParserVersion = 0,
        });
        await db.SaveChangesAsync();

        var packet = await db.Packets.SingleAsync(p => p.Id == 1);
        var service = CreateService();

        await service.ReprocessOneAsync(packet, db, "N0CALL-10", default);

        // Packet fields are re-derived from RawPacket...
        Assert.Multiple(() =>
        {
            Assert.That(packet.StationCallsign, Is.EqualTo("K4TUX-10"), "StationCallsign repaired from RawPacket");
            Assert.That(packet.ParserVersion, Is.EqualTo(ParserVersionInfo.Current), "version stamped");
            Assert.That(packet.ParsedType, Is.EqualTo(PacketType.Position), "type re-derived");
            Assert.That(packet.Latitude, Is.Not.Null, "position derived onto the packet");
        });

        // ...but the live Station record is untouched (no replayed side effects).
        var station = await db.Stations.FindAsync("K4TUX-10");
        Assert.Multiple(() =>
        {
            Assert.That(station!.LastLat, Is.EqualTo(10.0), "station position must not be overwritten by reprocessing");
            Assert.That(station.LastLon, Is.EqualTo(20.0));
        });
    }

    // Builds an AprsPacketParsingService with throwaway dependencies. ReprocessOneAsync
    // only uses the DbContext (passed in), options, and logger — the hub, scope factory,
    // alert channel, and message-sending service are never invoked in reprocess mode, so
    // throwing stubs are sufficient and assert that fact.
    private static AprsPacketParsingService CreateService()
    {
        var options = Options.Create(new DireControlOptions { OurCallsign = "N0CALL-10" });
        var messageSending = new MessageSendingService(
            new KissConnectionHolder(),
            new ThrowingScopeFactory(),
            options,
            NullLogger<MessageSendingService>.Instance);

        return new AprsPacketParsingService(
            new ThrowingScopeFactory(),
            new ThrowingHubContext(),
            options,
            messageSending,
            new PendingAlertChannel(),
            NullLogger<AprsPacketParsingService>.Instance);
    }

    // =========================================================================
    // DeleteOrphanStationsAsync — removes stations with no packets, keeps
    // watch-listed ones, and cascades the station's statistic row.
    // =========================================================================

    [Test]
    public async Task DeleteOrphanStationsAsync_RemovesZeroPacketStations_KeepsActiveAndWatchlisted()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var provider = new ServiceCollection()
            .AddDbContext<DireControlContext>(o => o.UseSqlite(connection))
            .BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
            db.Database.EnsureCreated();

            // Active: has a packet → keep.
            db.Stations.Add(new Station { Callsign = "ACTIVE", FirstSeen = DateTime.UtcNow, LastSeen = DateTime.UtcNow, Symbol = "/-" });
            db.Packets.Add(new Packet { StationCallsign = "ACTIVE", RawPacket = "ACTIVE>APRS:>x", ReceivedAt = DateTime.UtcNow });
            // Orphan: no packets → delete.
            db.Stations.Add(new Station { Callsign = "ORPHAN", FirstSeen = DateTime.UtcNow, LastSeen = DateTime.UtcNow, Symbol = "/-" });
            // Watch-listed orphan: no packets but protected → keep.
            db.Stations.Add(new Station { Callsign = "WATCHED", FirstSeen = DateTime.UtcNow, LastSeen = DateTime.UtcNow, Symbol = "/-", IsOnWatchList = true });
            // Orphan with a statistic → delete; statistic cascades away.
            db.Stations.Add(new Station { Callsign = "ORPHAN2", FirstSeen = DateTime.UtcNow, LastSeen = DateTime.UtcNow, Symbol = "/-" });
            db.StationStatistics.Add(new StationStatistic { Callsign = "ORPHAN2", LastComputedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var maintenance = new DatabaseMaintenanceService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeLifetime(),
            NullLogger<DatabaseMaintenanceService>.Instance);

        var deleted = await maintenance.DeleteOrphanStationsAsync();

        Assert.That(deleted, Is.EqualTo(2));

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
            var remaining = await db.Stations.Select(s => s.Callsign).OrderBy(c => c).ToListAsync();
            Assert.Multiple(() =>
            {
                Assert.That(remaining, Is.EqualTo(new[] { "ACTIVE", "WATCHED" }));
                Assert.That(db.StationStatistics.Any(), Is.False, "orphan station's statistic cascaded away");
            });
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("scope factory must not be used during reprocessing");
    }

    private sealed class ThrowingHubContext : IHubContext<PacketHub>
    {
        public IHubClients Clients => throw new InvalidOperationException("hub must not be used during reprocessing");
        public IGroupManager Groups => throw new InvalidOperationException("hub must not be used during reprocessing");
    }

    // =========================================================================
    // BuildQuery — reprocessing selection. Uses real SQLite so the predicate is
    // exercised as translated SQL.
    // =========================================================================

    [Test]
    public async Task BuildQuery_DefaultSelectsOnlyStaleVersions()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<DireControlContext>().UseSqlite(connection).Options;
        using var db = new DireControlContext(options);
        db.Database.EnsureCreated();

        Seed(db, id: 1, version: 0, source: PacketSource.Rf);                       // stale
        Seed(db, id: 2, version: ParserVersionInfo.Current, source: PacketSource.Rf); // current
        Seed(db, id: 3, version: 0, source: PacketSource.AprsIs);                   // stale
        await db.SaveChangesAsync();

        var stale = await PacketReprocessingService
            .BuildQuery(db.Packets.AsNoTracking(), new ReprocessFilter())
            .Select(p => p.Id)
            .OrderBy(id => id)
            .ToListAsync();

        Assert.That(stale, Is.EqualTo(new[] { 1, 3 }));
    }

    [Test]
    public async Task BuildQuery_ForceSelectsAllRegardlessOfVersion()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<DireControlContext>().UseSqlite(connection).Options;
        using var db = new DireControlContext(options);
        db.Database.EnsureCreated();

        Seed(db, id: 1, version: 0, source: PacketSource.Rf);
        Seed(db, id: 2, version: ParserVersionInfo.Current, source: PacketSource.Rf);
        await db.SaveChangesAsync();

        var all = await PacketReprocessingService
            .BuildQuery(db.Packets.AsNoTracking(), new ReprocessFilter { Force = true })
            .Select(p => p.Id)
            .ToListAsync();

        Assert.That(all, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task BuildQuery_SourceFilterRestrictsToSource()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<DireControlContext>().UseSqlite(connection).Options;
        using var db = new DireControlContext(options);
        db.Database.EnsureCreated();

        Seed(db, id: 1, version: 0, source: PacketSource.Rf);
        Seed(db, id: 2, version: 0, source: PacketSource.AprsIs);
        await db.SaveChangesAsync();

        var rfOnly = await PacketReprocessingService
            .BuildQuery(db.Packets.AsNoTracking(), new ReprocessFilter { Source = PacketSource.Rf })
            .Select(p => p.Id)
            .ToListAsync();

        Assert.That(rfOnly, Is.EqualTo(new[] { 1 }));
    }

    private static void Seed(DireControlContext db, int id, int version, PacketSource source)
    {
        var callsign = $"TEST-{id}";
        db.Stations.Add(new Station
        {
            Callsign = callsign,
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
            Symbol = "/-",
        });
        db.Packets.Add(new Packet
        {
            Id = id,
            StationCallsign = callsign,
            RawPacket = $"{callsign}>APRS:>test",
            ReceivedAt = DateTime.UtcNow,
            Source = source,
            ParsedType = PacketType.Status,
            ParserVersion = version,
        });
    }
}
