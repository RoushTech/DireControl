using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Regression tests for querying into the MessageData JSON column. With the old
/// HasConversion mapping these filters threw InvalidOperationException at runtime
/// (untranslatable member access through a value converter); the ToJson mapping
/// translates them to json_extract.
/// </summary>
[TestFixture]
public class MessageQueryTests
{
    private SqliteConnection _connection = null!;
    private DireControlContext _db = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<DireControlContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new DireControlContext(options);
        await _db.Database.EnsureCreatedAsync();

        _db.Stations.Add(new Station
        {
            Callsign = "W1ABC",
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
            Symbol = "/-",
        });
        _db.Packets.Add(new Packet
        {
            StationCallsign = "W1ABC",
            ReceivedAt = DateTime.UtcNow,
            RawPacket = "W1ABC>APRS::W3UWU    :hello there{1",
            ParsedType = PacketType.Message,
            MessageData = new MessageData { Addressee = "W3UWU", Text = "hello there", MessageId = "1" },
        });
        _db.Packets.Add(new Packet
        {
            StationCallsign = "W1ABC",
            ReceivedAt = DateTime.UtcNow,
            RawPacket = "W1ABC>APRS:>status",
            ParsedType = PacketType.Status,
        });
        await _db.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task FilterByAddressee_TranslatesToSql()
    {
        var count = await _db.Packets
            .AsNoTracking()
            .Where(p => p.ParsedType == PacketType.Message)
            .Where(p => p.MessageData != null && p.MessageData.Addressee.Contains("W3U"))
            .CountAsync();

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task FilterByText_TranslatesToSql()
    {
        var count = await _db.Packets
            .AsNoTracking()
            .Where(p => p.ParsedType == PacketType.Message)
            .Where(p => p.MessageData != null && p.MessageData.Text.Contains("hello"))
            .CountAsync();

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task FilterByText_NoMatch_ReturnsZero()
    {
        var count = await _db.Packets
            .AsNoTracking()
            .Where(p => p.MessageData != null && p.MessageData.Text.Contains("nomatch"))
            .CountAsync();

        Assert.That(count, Is.Zero);
    }

    [Test]
    public async Task MessageData_RoundTrips()
    {
        var packet = await _db.Packets
            .AsNoTracking()
            .SingleAsync(p => p.ParsedType == PacketType.Message);

        Assert.Multiple(() =>
        {
            Assert.That(packet.MessageData, Is.Not.Null);
            Assert.That(packet.MessageData!.Addressee, Is.EqualTo("W3UWU"));
            Assert.That(packet.MessageData.Text, Is.EqualTo("hello there"));
            Assert.That(packet.MessageData.MessageId, Is.EqualTo("1"));
        });
    }

    [Test]
    public async Task PacketWithoutMessageData_IsNull()
    {
        var packet = await _db.Packets
            .AsNoTracking()
            .SingleAsync(p => p.ParsedType == PacketType.Status);

        Assert.That(packet.MessageData, Is.Null);
    }
}
