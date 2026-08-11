using System.Collections.Concurrent;
using DireControl.Api.Controllers.Models;
using DireControl.Api.Hubs;
using DireControl.Api.Services.Ax25;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using DireControl.Modem.Ax25;
using DireControl.Modem.Ax25.Lapb;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services.Terminal;

/// <summary>
/// Bridges AX.25 sessions to the web terminal: owns the web-visible session
/// registry (outbound sessions opened from the UI, inbound sessions to the
/// station's base callsign, and local PMS previews), keeps a scrollback ring
/// per session, pushes output/state/stats over the terminal hub, and records
/// transcripts.  Also the inbound handler for the base callsign, so a station
/// connecting to us surfaces as a live web terminal tab.
/// </summary>
public sealed class TerminalSessionService(
    Ax25SessionManager sessionManager,
    TerminalTranscriptRecorder recorder,
    IHubContext<TerminalHub> hub,
    IOptions<DireControlOptions> options,
    IServiceScopeFactory scopeFactory,
    IServiceProvider services,
    PacketServicesRestartTrigger restartTrigger,
    ILogger<TerminalSessionService> logger) : BackgroundService, IAx25InboundHandler
{
    private const int ScrollbackBytes = 128 * 1024;
    private const int MaxUnpinnedPresets = 20;

    /// <summary>One web-visible session and its scrollback ring.</summary>
    private sealed class TerminalSession(IAx25Session link, TerminalSessionOrigin origin)
    {
        public IAx25Session Link { get; } = link;
        public TerminalSessionOrigin Origin { get; } = origin;
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public string? EndReason { get; set; }
        public volatile bool Ended;

        private readonly byte[] _ring = new byte[ScrollbackBytes];
        private long _head; // total received bytes ever
        private readonly Lock _ringLock = new();

        /// <summary>Appends received bytes; returns the stream offset the data starts at.</summary>
        public long Append(ReadOnlySpan<byte> data)
        {
            lock (_ringLock)
            {
                var start = _head;
                foreach (var b in data)
                    _ring[_head++ % ScrollbackBytes] = b;
                return start;
            }
        }

        /// <summary>Snapshot of the retained scrollback: (stream offset of first byte, bytes).</summary>
        public (long StartSeq, byte[] Data) Snapshot()
        {
            lock (_ringLock)
            {
                var length = (int)Math.Min(_head, ScrollbackBytes);
                var start = _head - length;
                var data = new byte[length];
                for (var i = 0; i < length; i++)
                    data[i] = _ring[(start + i) % ScrollbackBytes];
                return (start, data);
            }
        }
    }

    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = [];

    // ------------------------------------------------------------ public API

    public IReadOnlyList<TerminalSessionDto> ListSessions() =>
        [.. _sessions.Values.OrderBy(s => s.StartedAt).Select(ToDto)];

    public TerminalSessionDto? GetSession(string id) =>
        _sessions.TryGetValue(id, out var s) ? ToDto(s) : null;

    public (long StartSeq, byte[] Data)? SnapshotBuffer(string id) =>
        _sessions.TryGetValue(id, out var s) ? s.Snapshot() : null;

    /// <summary>Opens an outbound session from the web terminal.</summary>
    public async Task<TerminalSessionDto> OpenAsync(OpenTerminalSessionRequest request, CancellationToken ct)
    {
        var remote = Ax25Address.Parse(request.RemoteCallsign.Trim().ToUpperInvariant());
        Ax25Address? local = string.IsNullOrWhiteSpace(request.LocalCallsign)
            ? null
            : Ax25Address.Parse(request.LocalCallsign.Trim().ToUpperInvariant());
        var path = ParsePath(request.DigiPath);

        var config = await BuildConfigAsync(request, ct);
        var link = await sessionManager.ConnectAsync(
            remote, path, request.Channel, config, local, ct);

        var session = Track(link, TerminalSessionOrigin.Outbound);
        await UpsertPresetAsync(request, ct);
        return ToDto(session);
    }

    /// <summary>
    /// Opens a PMS preview: an in-process pipe served by the real PMS handler,
    /// no RF involved.  Fails when the PMS feature is not registered/enabled.
    /// </summary>
    public async Task<TerminalSessionDto?> OpenLocalPmsAsync(CancellationToken ct)
    {
        var pms = services.GetService<IPmsSessionServer>();
        if (pms is null)
            return null;

        var station = Ax25Address.Parse(options.Value.OurCallsign);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var setting = await db.UserSettings.FindAsync([1], ct);
        var pmsCall = new Ax25Address(station.Callsign, setting?.PmsSsid ?? 1);

        var (terminalEnd, serverEnd) = LocalPipeSession.CreatePair(station, pmsCall);
        var session = Track(terminalEnd, TerminalSessionOrigin.LocalPms);

        _ = Task.Run(() => pms.HandleSessionAsync(serverEnd, ct), ct)
            .ContinueWith(
                t => logger.LogError(t.Exception, "Local PMS preview handler failed."),
                TaskContinuationOptions.OnlyOnFaulted);

        return ToDto(session);
    }

    public async Task<bool> SendInputAsync(string id, byte[] data, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.Ended)
            return false;
        recorder.Append(id, TranscriptDirection.Sent, data);
        await session.Link.SendAsync(data, ct);
        return true;
    }

    public async Task<bool> CloseAsync(string id, bool abort, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var session))
            return false;

        if (abort || session.Ended)
            session.Link.Abort();
        else
            await session.Link.DisconnectAsync(ct);

        // Ended sessions stay in the list until closed so the tab shows the
        // disconnect; an explicit close after the end removes them.
        if (session.Ended)
        {
            _sessions.TryRemove(id, out _);
            await hub.Clients.All.SendAsync(TerminalHub.SessionsChangedMethod, cancellationToken: ct);
        }
        return true;
    }

    /// <summary>Inbound handler for the station's base callsign.</summary>
    public Task HandleSessionAsync(IAx25Session session, CancellationToken ct)
    {
        Track(session, TerminalSessionOrigin.Inbound);
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------- lifecycle

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var iterCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, restartTrigger.Token);
            var ct = iterCts.Token;

            // Claim the base callsign so inbound connects surface in the web
            // terminal; a settings save re-runs this with the fresh callsign.
            var baseCallsign = options.Value.OurCallsign;
            var registered = sessionManager.RegisterListener(baseCallsign, this);
            if (!registered)
                logger.LogWarning("Terminal could not claim {Callsign} — already registered.", baseCallsign);

            try
            {
                // 1 Hz stats push for live sessions.
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    foreach (var (id, session) in _sessions)
                    {
                        if (session.Ended)
                            continue;
                        await hub.Clients.Group(TerminalHub.GroupName(id)).SendAsync(
                            TerminalHub.StatsMethod,
                            new { SessionId = id, Stats = ToStatsDto(session.Link) },
                            ct);
                    }
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Settings changed — re-register and continue.
            }
            finally
            {
                if (registered)
                    sessionManager.UnregisterListener(baseCallsign);
            }
        }
    }

    // -------------------------------------------------------------- internals

    private TerminalSession Track(IAx25Session link, TerminalSessionOrigin origin)
    {
        var session = new TerminalSession(link, origin);
        _sessions[link.Id] = session;

        _ = recorder.StartAsync(
                link.Id, origin, link.Channel,
                link.Local.ToString(), link.Remote.ToString(),
                string.Join(',', link.Path))
            .ContinueWith(
                t => logger.LogError(t.Exception, "Transcript start failed for {Id}.", link.Id),
                TaskContinuationOptions.OnlyOnFaulted);

        link.StateChanged += (_, _, _) => PushState(session);
        link.Closed += (_, reason) =>
        {
            session.Ended = true;
            session.EndReason = reason.ToString();
            recorder.FinalizeAsync(link.Id, reason.ToString()).ContinueWith(
                t => logger.LogError(t.Exception, "Transcript finalize failed for {Id}.", link.Id),
                TaskContinuationOptions.OnlyOnFaulted);
            PushState(session);
        };

        _ = Task.Run(() => PumpReceivedAsync(session));
        _ = hub.Clients.All.SendAsync(TerminalHub.SessionsChangedMethod);
        return session;
    }

    private async Task PumpReceivedAsync(TerminalSession session)
    {
        var id = session.Link.Id;
        try
        {
            await foreach (var data in session.Link.Received.ReadAllAsync())
            {
                var seq = session.Append(data);
                recorder.Append(id, TranscriptDirection.Received, data);
                await hub.Clients.Group(TerminalHub.GroupName(id)).SendAsync(
                    TerminalHub.OutputMethod,
                    new { SessionId = id, Seq = seq, DataBase64 = Convert.ToBase64String(data) });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Terminal output pump failed for {Id}.", id);
        }
    }

    private void PushState(TerminalSession session)
    {
        _ = hub.Clients.Group(TerminalHub.GroupName(session.Link.Id))
            .SendAsync(TerminalHub.StateChangedMethod, ToDto(session));
        _ = hub.Clients.All.SendAsync(TerminalHub.SessionsChangedMethod);
    }

    private async Task<LapbConfig> BuildConfigAsync(OpenTerminalSessionRequest request, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var s = await db.UserSettings.FindAsync([1], ct);

        return new LapbConfig
        {
            RequestExtended = request.Mod128 ?? s?.ConnectedModePreferMod128 ?? false,
            PacLen = Math.Clamp(request.PacLen ?? s?.ConnectedModeDefaultPaclen ?? 128, 16, 256),
            WindowSize = Math.Clamp(request.WindowSize ?? s?.ConnectedModeWindowSize ?? 4, 1, 63),
            N2 = Math.Clamp(request.MaxRetries ?? s?.ConnectedModeRetries ?? 10, 1, 30),
            T1 = TimeSpan.FromSeconds(Math.Clamp(request.T1Seconds ?? s?.ConnectedModeT1Seconds ?? 3, 1, 60)),
        };
    }

    private async Task UpsertPresetAsync(OpenTerminalSessionRequest request, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
            var remote = request.RemoteCallsign.Trim().ToUpperInvariant();
            var digiPath = request.DigiPath?.Trim().ToUpperInvariant() ?? string.Empty;

            var preset = await db.TerminalPresets.FirstOrDefaultAsync(
                p => p.RemoteCallsign == remote && p.Channel == request.Channel && p.DigiPath == digiPath, ct);
            if (preset is null)
            {
                preset = new TerminalPreset
                {
                    Name = remote,
                    RemoteCallsign = remote,
                    Channel = request.Channel,
                    DigiPath = digiPath,
                    CreatedAt = DateTime.UtcNow,
                };
                db.TerminalPresets.Add(preset);
            }
            preset.LocalCallsign = string.IsNullOrWhiteSpace(request.LocalCallsign)
                ? null
                : request.LocalCallsign.Trim().ToUpperInvariant();
            preset.LastUsedAt = DateTime.UtcNow;
            preset.UseCount++;

            // Trim unpinned recents beyond the cap, oldest-used first.
            var excess = await db.TerminalPresets
                .Where(p => !p.IsPinned)
                .OrderByDescending(p => p.LastUsedAt)
                .Skip(MaxUnpinnedPresets)
                .ToListAsync(ct);
            db.TerminalPresets.RemoveRange(excess);

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Preset upsert failed — session opened regardless.");
        }
    }

    private static IReadOnlyList<Ax25Address>? ParsePath(string? digiPath) =>
        string.IsNullOrWhiteSpace(digiPath)
            ? null
            : digiPath
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(8)
                .Select(Ax25Address.Parse)
                .ToList();

    private static TerminalSessionDto ToDto(TerminalSession session) => new()
    {
        Id = session.Link.Id,
        Origin = session.Origin,
        State = MapState(session),
        Channel = session.Link.Channel,
        LocalCallsign = session.Link.Local.ToString(),
        RemoteCallsign = session.Link.Remote.ToString(),
        DigiPath = string.Join(',', session.Link.Path),
        StartedAt = session.StartedAt,
        EndReason = session.EndReason,
        Stats = ToStatsDto(session.Link),
    };

    private static TerminalSessionStatsDto ToStatsDto(IAx25Session link)
    {
        var stats = link.Stats;
        return new TerminalSessionStatsDto
        {
            Vs = stats.Vs,
            Vr = stats.Vr,
            Va = stats.Va,
            OutstandingIFrames = stats.OutstandingIFrames,
            RetryCount = stats.RetryCount,
            SendQueueDepth = stats.SendQueueDepth,
            BytesIn = stats.BytesIn,
            BytesOut = stats.BytesOut,
        };
    }

    private static TerminalSessionState MapState(TerminalSession session) => session.Link.State switch
    {
        _ when session.Ended => TerminalSessionState.Disconnected,
        LapbState.AwaitingConnection => TerminalSessionState.Connecting,
        LapbState.Connected or LapbState.TimerRecovery => TerminalSessionState.Connected,
        LapbState.AwaitingRelease => TerminalSessionState.Disconnecting,
        LapbState.Disconnected => TerminalSessionState.Disconnected,
        _ => TerminalSessionState.Unknown,
    };
}
