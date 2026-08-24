using DireControl.Api.Hubs;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using DireControl.Modem.Ax25;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Per-packet reception classification: <see cref="Packet.HeardVia"/> is stamped at parse
/// time so reception statistics can be aggregated in SQL instead of re-splitting every
/// stored path. These tests pin the classification for each packet-path shape the parser
/// meets, and the one case where a stored path must NOT be believed.
/// </summary>
[TestFixture]
public sealed class RfHeardClassificationTests
{
    private SqliteConnection _connection = null!;
    private DireControlContext _db = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<DireControlContext>().UseSqlite(_connection).Options;
        _db = new DireControlContext(options);
        _db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private int _nextPacketId = 1;

    /// <summary>Parses a stored packet and returns the classification the parser stamped.</summary>
    private async Task<HeardVia> ClassifyAsync(
        string rawPacket,
        PacketSource source = PacketSource.Rf,
        HeardVia existing = HeardVia.Unknown)
    {
        var callsign = rawPacket[..rawPacket.IndexOf('>')];
        var id = _nextPacketId++;

        if (await _db.Stations.FindAsync(callsign) is null)
        {
            _db.Stations.Add(new Station
            {
                Callsign = callsign,
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                Symbol = "/-",
            });
        }

        _db.Packets.Add(new Packet
        {
            Id = id,
            StationCallsign = callsign,
            RawPacket = rawPacket,
            ReceivedAt = DateTime.UtcNow,
            Source = source,
            HeardVia = existing,
            ParserVersion = 0,
        });
        await _db.SaveChangesAsync();

        var packet = await _db.Packets.SingleAsync(p => p.Id == id);
        await CreateService().ReprocessOneAsync(packet, _db, "N0CALL-10", default);

        return packet.HeardVia;
    }

    // =========================================================================
    // RF paths
    // =========================================================================

    [Test]
    public async Task BarePath_IsDirect()
    {
        var via = await ClassifyAsync("K4TUX-10>APRS:!3346.02N/08406.98W-Test");

        Assert.That(via, Is.EqualTo(HeardVia.Direct));
    }

    [Test]
    public async Task UnusedWideAliases_AreStillDirect()
    {
        // The station asked for digipeating but we heard the original transmission — no
        // digipeater has marked itself used, so this is a direct copy.
        var via = await ClassifyAsync("K4TUX-10>APRS,WIDE1-1,WIDE2-1:!3346.02N/08406.98W-Test");

        Assert.That(via, Is.EqualTo(HeardVia.Direct));
    }

    [Test]
    public async Task UsedDigipeater_IsDigi()
    {
        var via = await ClassifyAsync("K4TUX-10>APRS,WE4MB-3*,WIDE2:!3346.02N/08406.98W-Test");

        Assert.That(via, Is.EqualTo(HeardVia.Digi));
    }

    [Test]
    public async Task DigiBeforeAlias_IsDigi()
    {
        var via = await ClassifyAsync("K4TUX-10>APRS,W4CAT-2,WIDE2*:!3346.02N/08406.98W-Test");

        Assert.That(via, Is.EqualTo(HeardVia.Digi));
    }

    // =========================================================================
    // APRS-IS paths
    // =========================================================================

    [Test]
    public async Task InternetOriginated_IsInternet()
    {
        var via = await ClassifyAsync(
            "K4TUX-10>APRS,TCPIP*,qAC,T2USA:!3346.02N/08406.98W-Test",
            PacketSource.AprsIs);

        Assert.That(via, Is.EqualTo(HeardVia.Internet));
    }

    [Test]
    public async Task IgatedDirectFromRf_IsIgateRf()
    {
        var via = await ClassifyAsync(
            "K4TUX-10>APRS,qAR,WE4MB:!3346.02N/08406.98W-Test",
            PacketSource.AprsIs);

        Assert.That(via, Is.EqualTo(HeardVia.IgateRf));
    }

    [Test]
    public async Task ServerInjectedQConstructs_AreInternetNotUnknown()
    {
        // qAS (from a server) and qAU (unverified client) carry no RF leg of ours. Classifying
        // them rather than leaving them Unknown is what lets the backfill sweep converge —
        // Unknown must mean "not yet classified" and nothing else.
        var qas = await ClassifyAsync(
            "K4TUX-10>APRS,qAS,WXSVR-AU:!3346.02N/08406.98W-Test", PacketSource.AprsIs);
        var qau = await ClassifyAsync(
            "K4TUX-10>APRS,qAU,SOMECALL:!3346.02N/08406.98W-Test", PacketSource.AprsIs);

        Assert.Multiple(() =>
        {
            Assert.That(qas, Is.EqualTo(HeardVia.Internet));
            Assert.That(qau, Is.EqualTo(HeardVia.Internet));
        });
    }

    [Test]
    public async Task IgatedViaDigipeater_IsIgateRfDigi()
    {
        var via = await ClassifyAsync(
            "K4TUX-10>APRS,W4CAT-2*,WIDE2,qAR,WE4MB:!3346.02N/08406.98W-Test",
            PacketSource.AprsIs);

        Assert.That(via, Is.EqualTo(HeardVia.IgateRfDigi));
    }

    // =========================================================================
    // The RF-upgrade case: when an APRS-IS copy of a packet arrives before the RF
    // copy, ingest keeps the internet raw text but flips the row to Source.Rf. Its
    // stored path carries qAR, so re-deriving from that path alone would report our
    // own direct reception as igated and hide it from the direct-RF statistics.
    // =========================================================================

    [Test]
    public async Task ReprocessingAnUpgradedRow_KeepsTheClassificationFromTheFrameWeHeard()
    {
        var via = await ClassifyAsync(
            "K4TUX-10>APRS,qAR,WE4MB:!3346.02N/08406.98W-Test",
            PacketSource.Rf,
            existing: HeardVia.Direct);

        Assert.That(via, Is.EqualTo(HeardVia.Direct));
    }

    [Test]
    public async Task ReprocessingAnUnclassifiedRfRow_StillDerivesFromThePath()
    {
        // Nothing to preserve, so the stored path is the only evidence available.
        var via = await ClassifyAsync(
            "K4TUX-10>APRS,qAR,WE4MB:!3346.02N/08406.98W-Test",
            PacketSource.Rf,
            existing: HeardVia.Unknown);

        Assert.That(via, Is.EqualTo(HeardVia.IgateRf));
    }

    [Test]
    public void RfUpgrade_ClassifiesFromTheRfFrameNotTheStoredInternetCopy()
    {
        // What RfFrameIngestService does on the upgrade path: the RF frame it just decoded
        // has a bare path, even though the stored APRS-IS copy of the same info field went
        // through an igate.
        const string rfFrame = "K4TUX-10>APRS:!3346.02N/08406.98W-Test";
        const string internetCopy = "K4TUX-10>APRS,qAR,WE4MB:!3346.02N/08406.98W-Test";

        Assert.Multiple(() =>
        {
            Assert.That(ClassifyRaw(rfFrame), Is.EqualTo(HeardVia.Direct));
            Assert.That(ClassifyRaw(internetCopy), Is.EqualTo(HeardVia.IgateRf));
        });
    }

    // =========================================================================
    // Corpus sweep — the property the reception backfill depends on: Unknown must
    // mean "not yet classified" and nothing else, or the sweep re-reads the same
    // rows on every pass forever.
    // =========================================================================

    [Test]
    public void NoRealCapturedPath_ClassifiesAsUnknown()
    {
        var corpusPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "RawPackets.txt");
        if (!File.Exists(corpusPath))
            Assert.Ignore("RawPackets.txt corpus not present (local-only capture).");

        var unclassified = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checkedCount = 0;

        foreach (var line in File.ReadLines(corpusPath))
        {
            if (string.IsNullOrWhiteSpace(line) || !line.Contains('>') || !line.Contains(':'))
                continue;

            var (_, _, path) = DireControl.PathParsing.AprsPathParser.ParseTnc2Header(line);
            if (!seen.Add(path ?? string.Empty))
                continue;

            checkedCount++;
            if (ClassifyRaw(line) == HeardVia.Unknown)
                unclassified.Add(path ?? "(empty)");
        }

        Assert.Multiple(() =>
        {
            Assert.That(checkedCount, Is.GreaterThan(100), "corpus should cover many distinct paths");
            Assert.That(unclassified, Is.Empty,
                        $"paths left unclassified: {string.Join(" | ", unclassified.Take(10))}");
        });
    }

    private static HeardVia ClassifyRaw(string raw)
    {
        var (_, _, path) = DireControl.PathParsing.AprsPathParser.ParseTnc2Header(raw);
        return DireControl.PathParsing.AprsPathParser.ClassifyHeardVia(
            string.IsNullOrEmpty(path) ? [] : path.Split(',', StringSplitOptions.RemoveEmptyEntries));
    }

    // -------------------------------------------------------------------------
    // Harness — ReprocessOneAsync touches only the DbContext, options, and logger.
    // -------------------------------------------------------------------------

    private static AprsPacketParsingService CreateService()
    {
        var options = Options.Create(new DireControlOptions { OurCallsign = "N0CALL-10" });
        var messageSending = new MessageSendingService(
            new NullFrameTransmitter(),
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

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("scope factory must not be used during reprocessing");
    }

    private sealed class NullFrameTransmitter : IFrameTransmitter
    {
        public bool TrySend(byte[] ax25Frame, int channel = 0) => false;

        public bool TrySend(
            byte[] ax25Frame,
            int channel,
            TxPriority priority,
            TaskCompletionSource<bool>? txCompletion,
            bool exactChannelOnly = false) => false;
    }

    private sealed class ThrowingHubContext : IHubContext<PacketHub>
    {
        public IHubClients Clients =>
            throw new InvalidOperationException("hub must not be used during reprocessing");

        public IGroupManager Groups =>
            throw new InvalidOperationException("hub must not be used during reprocessing");
    }
}
