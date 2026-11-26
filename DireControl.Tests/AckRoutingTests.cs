using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using DireControl.Modem.Ax25;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// ACKs must go back the way the sender can hear them: RF ACKs use the
/// outbound digipeater path (not direct), and messages that arrived over
/// APRS-IS are ACKed on APRS-IS — a sender on another frequency never hears
/// an RF-only ACK.
/// </summary>
[TestFixture]
public sealed class AckRoutingTests
{
    private const string Our = "W3UWU";
    private const string Peer = "K4DDA-7";

    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;
    private RecordingTransmitter _transmitter = null!;
    private AprsIsTxQueue _isQueue = null!;
    private MessageSendingService _sender = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _services = new ServiceCollection()
            .AddDbContext<DireControlContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
            await db.Database.EnsureCreatedAsync();
            // The schema seeds the singleton settings row.
            var settings = await db.UserSettings.SingleAsync();
            settings.AprsIsEnabled = true;
            settings.OutboundPath = "WIDE1-1,WIDE2-1";
            settings.IsToRfRecentHeardMinutes = 30;
            await db.SaveChangesAsync();
        }

        _transmitter = new RecordingTransmitter();
        _isQueue = new AprsIsTxQueue();
        _sender = new MessageSendingService(
            _transmitter,
            _isQueue,
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DireControlOptions { OurCallsign = Our }),
            NullLogger<MessageSendingService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _services.DisposeAsync();
        _connection.Dispose();
    }

    private static byte[] ExpectedRfAck(string path) =>
        Ax25Encoder.EncodeUiFrame(Our, ":K4DDA-7  :ack87", path);

    private List<string> DrainIsQueue()
    {
        var lines = new List<string>();
        while (_isQueue.Reader.TryRead(out var line))
            lines.Add(line);
        return lines;
    }

    private async Task UpdateAsync(Action<DireControlContext> change)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        change(db);
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task RfMessage_AckedOnRf_WithOutboundPath()
    {
        await _sender.SendAckAsync(Peer, "87", PacketSource.Rf);

        Assert.That(_transmitter.Frames, Has.Count.EqualTo(1));
        Assert.That(_transmitter.Frames[0], Is.EqualTo(ExpectedRfAck("WIDE1-1,WIDE2-1")));
        Assert.That(DrainIsQueue(), Is.Empty);
    }

    [Test]
    public async Task RfMessage_AckedDirect_WhenOutboundPathEmpty()
    {
        await UpdateAsync(db => db.UserSettings.Single().OutboundPath = "");

        await _sender.SendAckAsync(Peer, "87", PacketSource.Rf);

        Assert.That(_transmitter.Frames[0], Is.EqualTo(ExpectedRfAck("")));
    }

    [Test]
    public async Task IsMessage_FromStationNotHeardOnRf_AckedOnAprsIsOnly()
    {
        await _sender.SendAckAsync(Peer, "87", PacketSource.AprsIs);

        Assert.That(DrainIsQueue(), Is.EqualTo(new[] { "W3UWU>APRS,TCPIP*::K4DDA-7  :ack87" }));
        Assert.That(_transmitter.Frames, Is.Empty);
    }

    [Test]
    public async Task IsMessage_FromStationRecentlyHeardOnRf_AckedOnBoth()
    {
        await UpdateAsync(db => db.Stations.Add(new Station
        {
            Callsign = Peer,
            Symbol = "/[",
            LastHeardRf = DateTime.UtcNow.AddMinutes(-5),
        }));

        await _sender.SendAckAsync(Peer, "87", PacketSource.AprsIs);

        Assert.That(DrainIsQueue(), Has.Count.EqualTo(1));
        Assert.That(_transmitter.Frames, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task IsMessage_FallsBackToRf_WhenAprsIsDisabled()
    {
        await UpdateAsync(db => db.UserSettings.Single().AprsIsEnabled = false);

        await _sender.SendAckAsync(Peer, "87", PacketSource.AprsIs);

        Assert.That(DrainIsQueue(), Is.Empty);
        Assert.That(_transmitter.Frames, Has.Count.EqualTo(1));
    }

    [Test]
    public void BuildIsOriginatedLine_UsesTcpipPath()
    {
        Assert.That(
            IgateLogic.BuildIsOriginatedLine(" w3uwu ", ":K4DDA-7  :ack87"),
            Is.EqualTo("W3UWU>APRS,TCPIP*::K4DDA-7  :ack87"));
    }

    private sealed class RecordingTransmitter : IFrameTransmitter
    {
        public List<byte[]> Frames { get; } = [];

        public bool TrySend(byte[] ax25Frame, int channel = 0)
        {
            Frames.Add(ax25Frame);
            return true;
        }

        public bool TrySend(
            byte[] ax25Frame,
            int channel,
            TxPriority priority,
            TaskCompletionSource<bool>? txCompletion,
            bool exactChannelOnly = false) => TrySend(ax25Frame, channel);
    }
}
