using DireControl.Api.Hubs;
using DireControl.Api.Services;
using DireControl.Api.Services.Ax25;
using DireControl.Data;
using DireControl.Data.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DireControl.Tests.Fakes;

/// <summary>
/// A real <see cref="RfFrameIngestService"/> over an in-memory SQLite
/// database, with every network-facing collaborator inert: KISS and AGWPE
/// servers with no clients, a no-op SignalR hub, and a transmitter that
/// records what it was asked to send.
/// </summary>
internal sealed class IngestHarness : IDisposable
{
    public const string OurCallsign = "N0CALL-10";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public AprsIsTxQueue AprsIsTxQueue { get; } = new();
    public RecordingFrameTransmitter Transmitter { get; } = new();
    public RfFrameIngestService Ingest { get; }
    public IServiceScopeFactory ScopeFactory { get; }

    public IngestHarness(Action<UserSetting>? configure = null)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<DireControlContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();
        ScopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();

        using (var scope = ScopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
            db.Database.EnsureCreated();
            var setting = db.UserSettings.Find(1);
            if (setting is null)
            {
                setting = new UserSetting { Id = 1 };
                db.UserSettings.Add(setting);
            }
            setting.AprsIsEnabled = true;
            setting.RfToIsGatingEnabled = true;
            configure?.Invoke(setting);
            db.SaveChanges();
        }

        var options = Options.Create(new DireControlOptions { OurCallsign = OurCallsign });
        var sessions = new Ax25SessionManager(
            Transmitter, ScopeFactory, options, NullLogger<Ax25SessionManager>.Instance);

        Ingest = new RfFrameIngestService(
            ScopeFactory,
            new DigipeaterService(ScopeFactory, Transmitter, options, NullLogger<DigipeaterService>.Instance),
            new KissTcpServerService(
                ScopeFactory, Transmitter, new RestartSignal(), NullLogger<KissTcpServerService>.Instance),
            new AgwpeTcpServerService(
                ScopeFactory, Transmitter, sessions, new RestartSignal(),
                NullLogger<AgwpeTcpServerService>.Instance),
            sessions,
            AprsIsTxQueue,
            new NullHubContext(),
            options,
            NullLogger<RfFrameIngestService>.Instance);
    }

    public DireControlContext CreateDb() =>
        ScopeFactory.CreateScope().ServiceProvider.GetRequiredService<DireControlContext>();

    /// <summary>Every line currently waiting in the RF→IS queue.</summary>
    public List<string> DrainGatedLines()
    {
        var lines = new List<string>();
        while (AprsIsTxQueue.Reader.TryRead(out var line))
            lines.Add(line);
        return lines;
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }
}

internal sealed class RecordingFrameTransmitter : IFrameTransmitter
{
    public List<byte[]> Sent { get; } = [];
    public bool Accept { get; set; } = true;

    /// <summary>Runs inside each accepted send — lets a test interleave a concurrent event.</summary>
    public Action? OnSend { get; set; }

    public bool TrySend(byte[] ax25Frame, int channel = 0) =>
        TrySend(ax25Frame, channel, TxPriority.Normal, null);

    public bool TrySend(
        byte[] ax25Frame, int channel, TxPriority priority,
        TaskCompletionSource<bool>? txCompletion, bool exactChannelOnly = false)
    {
        if (!Accept)
            return false;
        lock (Sent) Sent.Add(ax25Frame);
        OnSend?.Invoke();
        txCompletion?.TrySetResult(true);
        return true;
    }
}

internal sealed class NullHubContext : IHubContext<PacketHub>
{
    public IHubClients Clients { get; } = new NullHubClients();
    public IGroupManager Groups { get; } = new NullGroupManager();

    private sealed class NullClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NullHubClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new NullClientProxy();
        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName) => Proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class NullGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
