using System.Collections.Concurrent;
using System.Text;
using DireControl.Api.Services;
using DireControl.Api.Services.Ax25;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Modem.Ax25;
using DireControl.Modem.Ax25.Lapb;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace DireControl.Tests;

/// <summary>
/// Session-manager tests with a fake transmitter and an in-memory SQLite
/// settings store: inbound accept/refuse paths, self-echo and digi-transit
/// guards, and a full outbound↔inbound data round trip.
/// </summary>
[TestFixture]
public sealed class Ax25SessionManagerTests
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private RecordingTransmitter _transmitter = null!;
    private Ax25SessionManager _manager = null!;

    private const string OurCall = "W3UWU";

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<DireControlContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
            db.Database.EnsureCreated();
            var setting = db.UserSettings.Find(1)!;
            setting.ConnectedModeInboundEnabled = true;
            setting.ConnectedModeT1Seconds = 3;
            db.SaveChanges();
        }

        _transmitter = new RecordingTransmitter();
        _manager = new Ax25SessionManager(
            _transmitter,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DireControlOptions { OurCallsign = OurCall }),
            NullLogger<Ax25SessionManager>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private void SetInbound(bool enabled, int maxSessions = 10)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var setting = db.UserSettings.Find(1)!;
        setting.ConnectedModeInboundEnabled = enabled;
        setting.ConnectedModeMaxSessions = maxSessions;
        db.SaveChanges();
    }

    private static byte[] BuildFrame(
        string from, string to, Ax25FrameType type,
        int ns = 0, int nr = 0, bool pf = true, byte[]? info = null,
        IReadOnlyList<Ax25Address>? path = null, bool command = true)
    {
        var control = Ax25ControlField.Build(type, extended: false, ns, nr, pf);
        var frame = new Ax25Frame
        {
            Destination = Ax25Address.Parse(to),
            Source = Ax25Address.Parse(from),
            Path = path ?? [],
            Control = control[0],
            Pid = Ax25ControlField.HasPid(type) ? Ax25Frame.NoLayer3Pid : null,
            DestCommandBit = command,
            SourceCommandBit = !command,
            Info = info ?? [],
        };
        return Ax25Encoder.Encode(frame);
    }

    private bool Offer(byte[] raw, int channel = 0)
    {
        Assert.That(Ax25Decoder.TryDecode(raw, out var decoded), Is.True, "test frame must decode");
        return _manager.OfferFrame(raw, decoded, channel);
    }

    private static void WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Assert.That(condition(), Is.True, because);
    }

    // ---------------------------------------------------------------- offering

    [Test]
    public void UiFrames_NeverConsumed()
    {
        var ui = Ax25Encoder.EncodeUiFrame("KI4XYZ", ">status", "", OurCall);
        Assert.That(Offer(ui), Is.False, "UI frames belong to the APRS pipeline");
    }

    [Test]
    public void FramesStillInDigiTransit_NotConsumed()
    {
        _manager.RegisterListener(OurCall, new EchoHandler());
        var sabm = BuildFrame("KI4XYZ", OurCall, Ax25FrameType.SABM,
            path: [new Ax25Address("WIDE1", 1, HasBeenRepeated: false)]);

        Assert.That(Offer(sabm), Is.False,
            "unrepeated path entries mean the frame has not reached us yet");
    }

    [Test]
    public void DigipeatedSelfEcho_NotConsumed()
    {
        _manager.RegisterListener(OurCall, new EchoHandler());
        // Our own SABM comes back with the digi marked used and OUR call as source.
        var echo = BuildFrame(OurCall, "KI4XYZ", Ax25FrameType.SABM,
            path: [new Ax25Address("WIDE1", 1, HasBeenRepeated: true)]);

        Assert.That(Offer(echo), Is.False,
            "a session must never process its own digipeated transmissions");
    }

    [Test]
    public void InboundSabm_NoListener_NotConsumed()
    {
        var sabm = BuildFrame("KI4XYZ", "N0BODY", Ax25FrameType.SABM);
        Assert.That(Offer(sabm), Is.False, "not addressed to any claimed callsign");
    }

    [Test]
    public void InboundSabm_ListenerButInboundDisabled_GetsDm()
    {
        SetInbound(enabled: false);
        _manager.RegisterListener(OurCall, new EchoHandler());

        Assert.That(Offer(BuildFrame("KI4XYZ", OurCall, Ax25FrameType.SABM)), Is.True);

        WaitFor(() => _transmitter.Sent.Any(), "a refusal must be transmitted");
        var dm = Decode(_transmitter.Sent.Single().Frame);
        Assert.That(dm.FrameType, Is.EqualTo(Ax25FrameType.DM));
        Assert.That(dm.PollFinal, Is.True, "F echoes the received P");
        Assert.That(dm.Destination.ToString(), Is.EqualTo("KI4XYZ"));
        Assert.That(_manager.ActiveSessions, Is.Empty);
    }

    [Test]
    public void InboundSabm_Accepted_UaSentAndHandlerInvoked()
    {
        var handler = new EchoHandler();
        _manager.RegisterListener(OurCall, handler);

        Assert.That(Offer(BuildFrame("KI4XYZ", OurCall, Ax25FrameType.SABM)), Is.True);

        WaitFor(() => _transmitter.Sent.Any(f => Decode(f.Frame).FrameType == Ax25FrameType.UA),
            "the SABM must be answered UA");
        WaitFor(() => handler.Started, "the inbound handler must receive the session");

        var session = _manager.ActiveSessions.Single();
        Assert.That(session.IsInbound, Is.True);
        Assert.That(session.State, Is.EqualTo(LapbState.Connected));
        Assert.That(session.Remote.ToString(), Is.EqualTo("KI4XYZ"));
        Assert.That(session.Local.ToString(), Is.EqualTo(OurCall));
    }

    [Test]
    public void InboundSabm_MaxSessionsReached_GetsDm()
    {
        SetInbound(enabled: true, maxSessions: 1);
        _manager.RegisterListener(OurCall, new EchoHandler());

        Offer(BuildFrame("KI4XYZ", OurCall, Ax25FrameType.SABM));
        WaitFor(() => _manager.ActiveSessions.Count == 1, "first session accepted");
        _transmitter.SentQueue.Clear();

        Offer(BuildFrame("N4WR-2", OurCall, Ax25FrameType.SABM));

        WaitFor(() => _transmitter.Sent.Any(), "over-limit connect must be refused");
        Assert.That(Decode(_transmitter.Sent.Single().Frame).FrameType, Is.EqualTo(Ax25FrameType.DM));
        Assert.That(_manager.ActiveSessions, Has.Count.EqualTo(1));
    }

    [Test]
    public void StrayRrToClaimedCallsign_GetsDm()
    {
        _manager.RegisterListener(OurCall, new EchoHandler());

        Assert.That(Offer(BuildFrame("KI4XYZ", OurCall, Ax25FrameType.RR, nr: 3, pf: true)), Is.True);

        WaitFor(() => _transmitter.Sent.Any(), "disconnected-state rules answer DM");
        Assert.That(Decode(_transmitter.Sent.Single().Frame).FrameType, Is.EqualTo(Ax25FrameType.DM));
    }

    [Test]
    public void RegisterListener_SecondClaimRefused()
    {
        Assert.That(_manager.RegisterListener("W3UWU-1", new EchoHandler()), Is.True);
        Assert.That(_manager.RegisterListener("w3uwu-1", new EchoHandler()), Is.False, "case-insensitive");
        _manager.UnregisterListener("W3UWU-1");
        Assert.That(_manager.RegisterListener("W3UWU-1", new EchoHandler()), Is.True);
    }

    // ---------------------------------------------------------------- outbound

    [Test]
    public async Task Outbound_ConnectSendsSabm_SessionCreatedRaised()
    {
        IAx25Session? created = null;
        _manager.SessionCreated += s => created = s;

        var session = await _manager.ConnectAsync(Ax25Address.Parse("KB4BBS-7"), null, channel: 0);

        Assert.That(created, Is.SameAs(session));
        Assert.That(session.IsInbound, Is.False);
        WaitFor(() => _transmitter.Sent.Any(), "SABM must go out");
        var sabm = Decode(_transmitter.Sent.First().Frame);
        Assert.That(sabm.FrameType, Is.EqualTo(Ax25FrameType.SABM));
        Assert.That(sabm.Destination.ToString(), Is.EqualTo("KB4BBS-7"));
        Assert.That(sabm.Source.ToString(), Is.EqualTo(OurCall));
        Assert.That(_transmitter.Sent.First().ExactChannelOnly, Is.True,
            "session traffic must never hop radios");
        Assert.That(_transmitter.Sent.First().Priority, Is.EqualTo(TxPriority.Session));
    }

    [Test]
    public async Task Outbound_DuplicateSession_Throws()
    {
        await _manager.ConnectAsync(Ax25Address.Parse("KB4BBS-7"), null, channel: 0);
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.ConnectAsync(Ax25Address.Parse("KB4BBS-7"), null, channel: 0));
    }

    [Test]
    public async Task Outbound_DataRoundTrip()
    {
        var session = await _manager.ConnectAsync(Ax25Address.Parse("KB4BBS-7"), null, 0);

        // Peer answers UA — the frame comes from the remote to us.
        WaitFor(() => _transmitter.Sent.Any(), "SABM out");
        Offer(BuildFrame("KB4BBS-7", OurCall, Ax25FrameType.UA, pf: true, command: false));
        WaitFor(() => session.State == LapbState.Connected, "handshake completes");

        // Send data; the I frame must carry it.
        await session.SendAsync(Encoding.ASCII.GetBytes("hello bbs"));
        WaitFor(() => _transmitter.Sent.Any(f => Decode(f.Frame).FrameType == Ax25FrameType.I), "I frame out");
        var iFrame = Decode(_transmitter.Sent.First(f => Decode(f.Frame).FrameType == Ax25FrameType.I).Frame);
        Assert.That(Encoding.ASCII.GetString(iFrame.Info), Is.EqualTo("hello bbs"));

        // Peer acks and sends its own I frame; it must surface on Received.
        Offer(BuildFrame("KB4BBS-7", OurCall, Ax25FrameType.RR, nr: 1, pf: false, command: false));
        Offer(BuildFrame("KB4BBS-7", OurCall, Ax25FrameType.I, ns: 0, nr: 1, pf: false,
            info: Encoding.ASCII.GetBytes("welcome"), command: true));

        var received = await session.Received.ReadAsync(new CancellationTokenSource(5000).Token);
        Assert.That(Encoding.ASCII.GetString(received), Is.EqualTo("welcome"));
        Assert.That(session.Stats.BytesIn, Is.EqualTo("welcome".Length));
        Assert.That(session.Stats.BytesOut, Is.EqualTo("hello bbs".Length));

        // Remote disconnects; session closes and leaves the roster.
        LapbDisconnectReason? closedReason = null;
        session.Closed += (_, reason) => closedReason = reason;
        Offer(BuildFrame("KB4BBS-7", OurCall, Ax25FrameType.DISC, pf: true, command: true));
        WaitFor(() => closedReason is not null, "DISC closes the session");
        Assert.That(closedReason, Is.EqualTo(LapbDisconnectReason.RemoteDisc));
        Assert.That(_manager.ActiveSessions, Is.Empty);
    }

    // ---------------------------------------------------------------- helpers

    private static Ax25Frame Decode(byte[] raw)
    {
        Assert.That(Ax25Decoder.TryDecode(raw, out var frame), Is.True, "transmitted frame must decode");
        return frame;
    }

    private sealed record SentFrame(byte[] Frame, int Channel, TxPriority Priority, bool ExactChannelOnly);

    /// <summary>Records frames and reports instant airtime (completes the TCS).</summary>
    private sealed class RecordingTransmitter : IFrameTransmitter
    {
        public ConcurrentQueue<SentFrame> SentQueue { get; } = new();

        public List<SentFrame> Sent => [.. SentQueue];

        public bool TrySend(byte[] ax25Frame, int channel = 0) =>
            TrySend(ax25Frame, channel, TxPriority.Normal, null);

        public bool TrySend(
            byte[] ax25Frame,
            int channel,
            TxPriority priority,
            TaskCompletionSource<bool>? txCompletion,
            bool exactChannelOnly = false)
        {
            SentQueue.Enqueue(new SentFrame(ax25Frame, channel, priority, exactChannelOnly));
            txCompletion?.TrySetResult(true);
            return true;
        }
    }

    private sealed class EchoHandler : IAx25InboundHandler
    {
        public volatile bool Started;

        public Task HandleSessionAsync(IAx25Session session, CancellationToken ct)
        {
            Started = true;
            return Task.CompletedTask;
        }
    }
}
