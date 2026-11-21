using DireControl.Api.Controllers;
using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using DireControl.Data.Models;
using DireControl.Enums;
using DireControl.Tests.Fakes;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Messages › All: filtering and paging run in SQL (83k+ rows is normal), the
/// filters are case-insensitive, and they translate on SQLite at all —
/// MessageData is a JSON-converted column, so it cannot be filtered on.
/// </summary>
[TestFixture]
public sealed class AllMessagesSearchTests
{
    private static Packet Msg(string from, string to, string text, int minutesAgo) => new()
    {
        StationCallsign = from,
        RawPacket = $"{from}>APRS,WIDE2-1::{to,-9}:{text}",
        InfoField = $":{to,-9}:{text}",
        ParsedType = PacketType.Message,
        Source = PacketSource.Rf,
        ReceivedAt = DateTime.UtcNow.AddMinutes(-minutesAgo),
        MessageData = new MessageData { Addressee = to, Text = text },
    };

    private static async Task<PaginatedResponse<AllMessagePacketDto>> Query(
        IngestHarness harness, int page = 1, int pageSize = 50,
        string? sender = null, string? addressee = null, string? text = null)
    {
        await using var db = harness.CreateDb();
        var controller = new MessagesController(
            db,
            Microsoft.Extensions.Options.Options.Create(new DireControlOptions()),
            null!,
            new TxFrameRegistry(TimeProvider.System),
            new NullHubContext());
        var result = await controller.GetAllMessages(page, pageSize, sender, addressee, text, default);
        return (PaginatedResponse<AllMessagePacketDto>)((Microsoft.AspNetCore.Mvc.OkObjectResult)result.Result!).Value!;
    }

    private static async Task<IngestHarness> Seeded()
    {
        var harness = new IngestHarness();
        await using var db = harness.CreateDb();
        foreach (var call in new[] { "W4BOC-1", "N1DTA-12", "K4DDA-7" })
            db.Stations.Add(new Station
            {
                Callsign = call,
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                Symbol = "/-",
            });
        db.Packets.AddRange(
            Msg("W4BOC-1", "W4BOC-1", "BITS.11111111,W4BOC Battery", 1),
            Msg("N1DTA-12", "N1DTA-12", "PARM.Vin,Rx1h", 2),
            Msg("K4DDA-7", "W3UWU", "APRS can be finicky 100%_sure", 3));
        await db.SaveChangesAsync();
        return harness;
    }

    [Test]
    public async Task SenderFilter_IsCaseInsensitive()
    {
        using var harness = await Seeded();
        var r = await Query(harness, sender: "w4boc");
        Assert.That(r.Items.Select(i => i.FromCallsign), Is.EqualTo(new[] { "W4BOC-1" }));
        Assert.That(r.TotalCount, Is.EqualTo(1));
    }

    [Test]
    public async Task AddresseeFilter_MatchesTheAddresseeField()
    {
        using var harness = await Seeded();
        var r = await Query(harness, addressee: "w3uwu");
        Assert.That(r.Items.Select(i => i.FromCallsign), Is.EqualTo(new[] { "K4DDA-7" }));
        Assert.That(r.Items[0].ToCallsign, Is.EqualTo("W3UWU"));
    }

    [Test]
    public async Task TextFilter_IsCaseInsensitive_AndTreatsWildcardsLiterally()
    {
        using var harness = await Seeded();
        Assert.That((await Query(harness, text: "battery")).Items.Select(i => i.FromCallsign),
            Is.EqualTo(new[] { "W4BOC-1" }));
        Assert.That((await Query(harness, text: "100%_s")).Items.Select(i => i.FromCallsign),
            Is.EqualTo(new[] { "K4DDA-7" }));
        Assert.That((await Query(harness, text: "%")).TotalCount, Is.EqualTo(1),
            "a bare % matches only text that contains one");
    }

    [Test]
    public async Task Paging_IsNewestFirst_WithTheFullTotal()
    {
        using var harness = await Seeded();
        var p2 = await Query(harness, page: 2, pageSize: 2);
        Assert.That(p2.TotalCount, Is.EqualTo(3));
        Assert.That(p2.Items.Select(i => i.FromCallsign), Is.EqualTo(new[] { "K4DDA-7" }));
    }
}
