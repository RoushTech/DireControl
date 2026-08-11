using System.Collections.Concurrent;
using DireControl.Data;
using DireControl.Modem.Ax25;
using DireControl.Modem.Ax25.Lapb;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services.Ax25;

/// <summary>
/// Owns every connected-mode AX.25 (LAPB) session.  Outbound sessions come
/// from <see cref="ConnectAsync"/>; inbound SABMs are accepted for callsigns
/// claimed via <see cref="RegisterListener"/> (the PMS, the web terminal, and
/// AGWPE clients each claim theirs at runtime).  Frames reach it through
/// <see cref="OfferFrame"/> on the shared RF ingest path.
/// </summary>
public sealed class Ax25SessionManager(
    IFrameTransmitter transmitter,
    IServiceScopeFactory scopeFactory,
    IOptions<DireControlOptions> options,
    ILogger<Ax25SessionManager> logger,
    IHostApplicationLifetime? lifetime = null)
{
    private readonly record struct SessionKey(
        string LocalCall, int LocalSsid, string RemoteCall, int RemoteSsid, int Channel)
    {
        public static SessionKey For(Ax25Address local, Ax25Address remote, int channel) => new(
            local.Callsign.ToUpperInvariant(), local.Ssid,
            remote.Callsign.ToUpperInvariant(), remote.Ssid, channel);
    }

    private readonly ConcurrentDictionary<SessionKey, Ax25SessionInternal> _sessions = [];
    private readonly ConcurrentDictionary<string, IAx25InboundHandler> _listeners = [];

    /// <summary>Raised for every new session, inbound and outbound.</summary>
    public event Action<IAx25Session>? SessionCreated;

    public IReadOnlyList<IAx25Session> ActiveSessions => [.. _sessions.Values];

    /// <summary>
    /// Claims a local callsign for inbound connections.  Returns
    /// <see langword="false"/> when another handler already holds it.
    /// </summary>
    public bool RegisterListener(string localCallsign, IAx25InboundHandler handler) =>
        _listeners.TryAdd(Normalize(localCallsign), handler);

    public void UnregisterListener(string localCallsign) =>
        _listeners.TryRemove(Normalize(localCallsign), out _);

    /// <summary>
    /// Opens an outbound session.  Returns immediately with the session in
    /// AwaitingConnection; watch <see cref="IAx25Session.StateChanged"/> /
    /// <see cref="IAx25Session.Closed"/> for the outcome.
    /// </summary>
    public async Task<IAx25Session> ConnectAsync(
        Ax25Address remote,
        IReadOnlyList<Ax25Address>? viaPath,
        int channel,
        LapbConfig? overrides = null,
        Ax25Address? local = null,
        CancellationToken ct = default)
    {
        var (defaults, _, maxSessions) = await GetDefaultsAsync(ct);
        var localAddr = local ?? Ax25Address.Parse(options.Value.OurCallsign);
        var path = (viaPath ?? []).Select(p => new Ax25Address(p.Callsign, p.Ssid)).ToList();
        var config = ScaleT1(overrides ?? defaults, path.Count);

        if (_sessions.Count >= maxSessions)
            throw new InvalidOperationException($"Session limit reached ({maxSessions}).");

        var key = SessionKey.For(localAddr, remote, channel);
        var session = new Ax25SessionInternal(
            localAddr, remote, path, channel, isInbound: false, config, transmitter, logger);

        if (!_sessions.TryAdd(key, session))
        {
            session.Dispose();
            throw new InvalidOperationException(
                $"A session {localAddr}→{remote} already exists on channel {channel}.");
        }

        session.Closed += (_, _) => _sessions.TryRemove(key, out _);
        SessionCreated?.Invoke(session);
        session.StartOutbound();
        return session;
    }

    /// <summary>
    /// Offers one RF-received frame (already decoded modulo-8) to the session
    /// layer.  Returns <see langword="true"/> when the frame was consumed by
    /// connected-mode handling and must not continue down the APRS pipeline.
    /// Cheap checks run inline on the ingest path; real work is posted to the
    /// owning session's mailbox or a background task.
    /// </summary>
    public bool OfferFrame(byte[] rawFrame, Ax25Frame decoded, int kissChannel)
    {
        // UI frames are never session traffic — APRS handles them.
        if (decoded.FrameType == Ax25FrameType.UI)
            return false;

        var source = decoded.Source;
        var dest = decoded.Destination;
        var sourceKeyed = Normalize(source.ToString());

        // A digipeated echo of our own transmission: the direct modem loopback
        // is flagged isOwnTransmission upstream, but a copy repeated back by a
        // digi is not. Without this a session would REJ its own I frames.
        if (_listeners.ContainsKey(sourceKeyed)
            || _sessions.Values.Any(s => AddressEquals(s.Local, source)))
        {
            return false;
        }

        // Not for us yet: unrepeated path entries mean the frame is still in
        // digi transit (our digipeater may need to serve it — not consume it).
        if (decoded.Path.Any(p => !p.HasBeenRepeated))
            return false;

        // Traffic for an active session.
        if (_sessions.TryGetValue(SessionKey.For(dest, source, kissChannel), out var session))
        {
            session.OfferReceivedFrame(rawFrame, decoded);
            return true;
        }

        // No session — but addressed to a claimed callsign.
        if (_listeners.TryGetValue(Normalize(dest.ToString()), out var handler))
        {
            if (decoded.FrameType is Ax25FrameType.SABM or Ax25FrameType.SABME)
            {
                _ = HandleInboundConnectAsync(decoded, kissChannel, handler)
                    .ContinueWith(
                        t => logger.LogError(t.Exception, "Unhandled inbound-connect error."),
                        TaskContinuationOptions.OnlyOnFaulted);
            }
            else
            {
                // Stray I/S/DISC with no link: disconnected-state rules say DM.
                SendDm(decoded, kissChannel);
            }
            return true;
        }

        return false;
    }

    private async Task HandleInboundConnectAsync(
        Ax25Frame sabm, int kissChannel, IAx25InboundHandler handler)
    {
        var (defaults, inboundEnabled, maxSessions) = await GetDefaultsAsync(CancellationToken.None);

        if (!inboundEnabled || _sessions.Count >= maxSessions)
        {
            SendDm(sabm, kissChannel);
            return;
        }

        var local = new Ax25Address(sabm.Destination.Callsign, sabm.Destination.Ssid);
        var remote = new Ax25Address(sabm.Source.Callsign, sabm.Source.Ssid);
        // Answer back through the digis that carried the SABM, reversed.
        var replyPath = sabm.Path
            .Select(p => new Ax25Address(p.Callsign, p.Ssid))
            .Reverse()
            .ToList();
        var config = ScaleT1(defaults, replyPath.Count);

        var key = SessionKey.For(local, remote, kissChannel);
        var session = new Ax25SessionInternal(
            local, remote, replyPath, kissChannel, isInbound: true, config, transmitter, logger);

        if (!_sessions.TryAdd(key, session))
        {
            // Simultaneous SABMs — the existing session's machine handles the
            // duplicate as a link reset; this late copy is dropped.
            session.Dispose();
            return;
        }

        session.Closed += (_, _) => _sessions.TryRemove(key, out _);
        session.AcceptIncoming(sabm);
        SessionCreated?.Invoke(session);

        var ct = lifetime?.ApplicationStopping ?? CancellationToken.None;
        _ = Task.Run(() => handler.HandleSessionAsync(session, ct), ct)
            .ContinueWith(
                t => logger.LogError(t.Exception, "Inbound session handler failed for {Remote}.", remote),
                TaskContinuationOptions.OnlyOnFaulted);

        logger.LogInformation(
            "Accepted inbound AX.25 connection {Remote}→{Local} on channel {Channel}.",
            remote, local, kissChannel);
    }

    /// <summary>DM response (F echoing the received P) back along the reversed path.</summary>
    private void SendDm(Ax25Frame received, int kissChannel)
    {
        var control = Ax25ControlField.Build(
            Ax25FrameType.DM, extended: false, ns: 0, nr: 0, pollFinal: received.PollFinal);
        var dm = new Ax25Frame
        {
            Destination = new Ax25Address(received.Source.Callsign, received.Source.Ssid),
            Source = new Ax25Address(received.Destination.Callsign, received.Destination.Ssid),
            Path = received.Path.Select(p => new Ax25Address(p.Callsign, p.Ssid)).Reverse().ToList(),
            Control = control[0],
            Pid = null,
            DestCommandBit = false,
            SourceCommandBit = true,
        };
        transmitter.TrySend(Ax25Encoder.Encode(dm), kissChannel);
    }

    private async Task<(LapbConfig Config, bool InboundEnabled, int MaxSessions)> GetDefaultsAsync(
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var s = await db.UserSettings.FindAsync([1], ct);
        var config = new LapbConfig
        {
            RequestExtended = s?.ConnectedModePreferMod128 ?? false,
            WindowSize = s?.ConnectedModeWindowSize ?? 4,
            PacLen = s?.ConnectedModeDefaultPaclen ?? 128,
            T1 = TimeSpan.FromSeconds(s?.ConnectedModeT1Seconds ?? 3),
            N2 = s?.ConnectedModeRetries ?? 10,
        };
        return (config, s?.ConnectedModeInboundEnabled ?? false, s?.ConnectedModeMaxSessions ?? 10);
    }

    /// <summary>Each digi hop adds two airtime legs; T1 must cover the round trip.</summary>
    private static LapbConfig ScaleT1(LapbConfig config, int hops) =>
        hops == 0 ? config : config with { T1 = config.T1 * (1 + 2 * hops) };

    private static string Normalize(string callsign) => callsign.Trim().ToUpperInvariant();

    private static bool AddressEquals(Ax25Address a, Ax25Address b) =>
        a.Ssid == b.Ssid && string.Equals(a.Callsign, b.Callsign, StringComparison.OrdinalIgnoreCase);
}
