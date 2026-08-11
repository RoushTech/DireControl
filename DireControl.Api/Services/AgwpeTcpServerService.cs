using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DireControl.Api.Services.Ax25;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Modem.Agwpe;
using DireControl.Modem.Ax25;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Services;

/// <summary>
/// AGWPE TCP server: the protocol third-party packet applications (Winlink
/// Express, Outpost, UZ7HO-style clients) speak to a TNC host.  Supports
/// monitoring ('M'/'m', 'U'/'I'/'S'), raw frames ('k'/'K'), UI transmit
/// ('V'), callsign registration ('X'/'x' — which also accepts inbound
/// connections for that callsign), and connected mode ('C'/'v'/'D'/'d',
/// 'Y'/'y') riding the shared LAPB session manager.  AGWPE has no real
/// authentication ('P' logins are read and ignored), so the listener binds
/// loopback unless deliberately configured otherwise.
/// </summary>
public sealed class AgwpeTcpServerService(
    IServiceScopeFactory scopeFactory,
    IFrameTransmitter transmitter,
    Ax25SessionManager sessionManager,
    PacketServicesRestartTrigger restartTrigger,
    ILogger<AgwpeTcpServerService> logger) : BackgroundService
{
    private sealed record PortInfo(int Channel, string Description, Radio Radio);

    private sealed class AgwpeClient(TcpClient tcp)
    {
        public TcpClient Tcp { get; } = tcp;
        public Lock WriteLock { get; } = new();
        public Lock StateLock { get; } = new();
        public volatile bool MonitorEnabled;
        public volatile bool RawEnabled;
        public HashSet<string> RegisteredCallsigns { get; } = [];
        public Dictionary<(byte Port, string Own, string Remote), IAx25Session> Connections { get; } = [];
    }

    private readonly Lock _clientsLock = new();
    private readonly List<AgwpeClient> _clients = [];
    private volatile List<PortInfo> _ports = [];

    public int ClientCount
    {
        get { lock (_clientsLock) return _clients.Count; }
    }

    /// <summary>
    /// Feeds one RF frame to monitoring/raw clients.  Fire-and-forget from the
    /// ingest path — dead clients are dropped, never block ingest.
    /// </summary>
    public void Broadcast(byte[] ax25Frame, int channel, bool isOwnTransmission)
    {
        List<AgwpeClient> clients;
        lock (_clientsLock)
        {
            if (_clients.Count == 0)
                return;
            clients = [.. _clients];
        }

        if (!Ax25Decoder.TryDecode(ax25Frame, out var frame))
            return;

        var port = PortForChannel(channel);
        var kind = isOwnTransmission ? 'T' : AgwpeMonitorFormatter.MonitorKind(frame.FrameType);
        byte[]? monitorPayload = null;
        byte[]? rawPayload = null;

        foreach (var client in clients)
        {
            if (client.RawEnabled)
            {
                rawPayload ??= BuildRawPayload(port, ax25Frame);
                Send(client, AgwpeFrame.Create('K',
                    port, frame.Source.ToString(), frame.Destination.ToString(), rawPayload));
            }
            else if (client.MonitorEnabled)
            {
                monitorPayload ??= AgwpeMonitorFormatter.Format(frame, port + 1, DateTime.Now);
                Send(client, AgwpeFrame.Create(kind,
                    port, frame.Source.ToString(), frame.Destination.ToString(), monitorPayload,
                    frame.Pid ?? 0));
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var iterCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, restartTrigger.Token);
            var ct = iterCts.Token;

            try
            {
                var settings = await GetSettingsAsync(stoppingToken);
                if (settings is not { AgwpeServerEnabled: true })
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    continue;
                }

                _ports = await LoadPortsAsync(stoppingToken);
                var bind = IPAddress.TryParse(settings.AgwpeServerBindAddress, out var ip)
                    ? ip
                    : IPAddress.Loopback;
                await ListenAsync(bind, settings.AgwpeServerPort, ct);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                continue; // settings changed — re-read and re-listen
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AGWPE server error; retrying in 10 s.");
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { }
            }
        }

        logger.LogInformation("AgwpeTcpServerService stopped.");
    }

    private async Task ListenAsync(IPAddress bind, int port, CancellationToken ct)
    {
        var listener = new TcpListener(bind, port);
        listener.Start();
        logger.LogInformation("AGWPE server listening on {Bind}:{Port}.", bind, port);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                var client = new AgwpeClient(tcp);
                lock (_clientsLock)
                    _clients.Add(client);
                logger.LogInformation(
                    "AGWPE client connected from {Remote} ({Count} total).",
                    tcp.Client.RemoteEndPoint, ClientCount);
                _ = Task.Run(() => ServeClientAsync(client, ct), ct);
            }
        }
        finally
        {
            listener.Stop();
            List<AgwpeClient> clients;
            lock (_clientsLock)
            {
                clients = [.. _clients];
                _clients.Clear();
            }
            foreach (var client in clients)
                CleanUpClient(client);
        }
    }

    private async Task ServeClientAsync(AgwpeClient client, CancellationToken ct)
    {
        var accumulated = new List<byte>();
        var buffer = new byte[8192];
        try
        {
            var stream = client.Tcp.GetStream();
            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                    break;
                accumulated.AddRange(buffer.AsSpan(0, read));

                while (AgwpeCodec.TryDecode(accumulated, out var frame))
                    await HandleClientFrameAsync(client, frame, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException
            or OperationCanceledException or InvalidDataException)
        {
            // Client gone, shutdown, or desynchronised stream — drop it.
        }
        finally
        {
            DropClient(client);
            logger.LogInformation("AGWPE client disconnected ({Count} remaining).", ClientCount);
        }
    }

    private async Task HandleClientFrameAsync(AgwpeClient client, AgwpeFrame frame, CancellationToken ct)
    {
        switch (frame.Kind)
        {
            case 'R': // version query
            {
                var data = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 2005);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 127);
                Send(client, AgwpeFrame.Create('R', data: data));
                break;
            }

            case 'G': // port list
            {
                var ports = _ports;
                var text = ports.Count == 0
                    ? "0;"
                    : $"{ports.Count};{string.Join(';', ports.Select((p, i) => $"Port{i + 1} {p.Description}"))};";
                Send(client, AgwpeFrame.Create('G', data: Encoding.ASCII.GetBytes(text)));
                break;
            }

            case 'g': // port capabilities
            {
                var radio = PortRadio(frame.Port);
                var data = new byte[12];
                data[0] = 0;                                              // on-air rate (0 = 1200)
                data[2] = (byte)Math.Clamp((radio?.TxDelayMs ?? 300) / 10, 0, 255);
                data[3] = (byte)Math.Clamp((radio?.TxTailMs ?? 50) / 10, 0, 255);
                data[4] = (byte)Math.Clamp(radio?.TxPersistence ?? 63, 0, 255);
                data[5] = (byte)Math.Clamp((radio?.TxSlotTimeMs ?? 100) / 10, 0, 255);
                data[6] = 4;                                              // maxframe
                data[7] = (byte)CountConnections(client, frame.Port);
                Send(client, AgwpeFrame.Create('g', frame.Port, data: data));
                break;
            }

            case 'X': // register callsign (also accepts inbound connects for it)
            {
                var callsign = frame.CallFrom.ToUpperInvariant();
                var handler = new ClientInboundHandler(this, client);
                var success = callsign.Length > 0 && sessionManager.RegisterListener(callsign, handler);
                if (success)
                {
                    lock (client.StateLock)
                        client.RegisteredCallsigns.Add(callsign);
                }
                Send(client, AgwpeFrame.Create('X', frame.Port, callsign, data: [(byte)(success ? 1 : 0)]));
                break;
            }

            case 'x':
            {
                var callsign = frame.CallFrom.ToUpperInvariant();
                bool removed;
                lock (client.StateLock)
                    removed = client.RegisteredCallsigns.Remove(callsign);
                if (removed)
                    sessionManager.UnregisterListener(callsign);
                break;
            }

            case 'M':
                client.MonitorEnabled = true;
                break;
            case 'm':
                client.MonitorEnabled = false;
                break;
            case 'k':
                client.RawEnabled = !client.RawEnabled;
                break;

            case 'K': // raw AX.25 transmit: data = 1-byte port + frame
            {
                if (frame.Data.Length < 2)
                    break;
                var channel = ChannelForPort(frame.Data[0]);
                if (transmitter.TrySend(frame.Data[1..], channel))
                    Broadcast(frame.Data[1..], channel, isOwnTransmission: true);
                break;
            }

            case 'V': // UI transmit with via path
            {
                var (path, info) = ParseViaPayload(frame.Data);
                var pathText = string.Join(',', path);
                var raw = Ax25Encoder.EncodeUiFrame(
                    frame.CallFrom, Encoding.ASCII.GetString(info), pathText, frame.CallTo);
                if (transmitter.TrySend(raw, ChannelForPort(frame.Port)))
                    Broadcast(raw, ChannelForPort(frame.Port), isOwnTransmission: true);
                break;
            }

            case 'C':
            case 'v':
                await OpenConnectionAsync(client, frame, ct);
                break;

            case 'D':
            {
                if (FindConnection(client, frame.Port, frame.CallFrom, frame.CallTo) is { } session)
                    await session.SendAsync(frame.Data, ct);
                break;
            }

            case 'd':
            {
                if (FindConnection(client, frame.Port, frame.CallFrom, frame.CallTo) is { } session)
                    _ = session.DisconnectAsync(CancellationToken.None); // 'd' notif follows from Closed
                break;
            }

            case 'Y': // outstanding frames on one connection
            {
                var session = FindConnection(client, frame.Port, frame.CallFrom, frame.CallTo);
                var count = session is null ? 0 : session.Stats.OutstandingIFrames + session.Stats.SendQueueDepth;
                var data = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)count);
                Send(client, AgwpeFrame.Create('Y', frame.Port, frame.CallFrom, frame.CallTo, data));
                break;
            }

            case 'y': // outstanding frames on a port
            {
                int total;
                lock (client.StateLock)
                {
                    total = client.Connections
                        .Where(kv => kv.Key.Port == frame.Port)
                        .Sum(kv => kv.Value.Stats.OutstandingIFrames + kv.Value.Stats.SendQueueDepth);
                }
                var data = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)total);
                Send(client, AgwpeFrame.Create('y', frame.Port, data: data));
                break;
            }

            case 'P': // login — no authentication in AGWPE; read and ignore
                break;

            case 'H': // heard list — unsupported in v1
                break;

            default:
                logger.LogDebug("AGWPE client sent unsupported frame kind '{Kind}'.", frame.Kind);
                break;
        }
    }

    private async Task OpenConnectionAsync(AgwpeClient client, AgwpeFrame frame, CancellationToken ct)
    {
        var own = frame.CallFrom.ToUpperInvariant();
        var remote = frame.CallTo.ToUpperInvariant();
        try
        {
            IReadOnlyList<Ax25Address>? path = null;
            if (frame.Kind == 'v')
                path = ParseViaPayload(frame.Data).Path.Select(Ax25Address.Parse).ToList();

            var session = await sessionManager.ConnectAsync(
                Ax25Address.Parse(remote), path, ChannelForPort(frame.Port),
                local: Ax25Address.Parse(own), ct: ct);
            BindSession(client, session, connectNotifOnConnected: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AGWPE connect {Own}→{Remote} failed.", own, remote);
            Send(client, AgwpeFrame.Create('d', frame.Port, remote, own,
                Encoding.ASCII.GetBytes($"*** DISCONNECTED From Station {remote}\r")));
        }
    }

    /// <summary>Wires a session (outbound or inbound) to its owning AGWPE client.</summary>
    private void BindSession(AgwpeClient client, IAx25Session session, bool connectNotifOnConnected)
    {
        var port = PortForChannel(session.Channel);
        var own = session.Local.ToString();
        var remote = session.Remote.ToString();

        lock (client.StateLock)
            client.Connections[(port, own, remote)] = session;

        if (connectNotifOnConnected)
        {
            // Outbound: the exact string clients expect on link-up.
            session.StateChanged += (s, _, to) =>
            {
                if (to == Modem.Ax25.Lapb.LapbState.Connected)
                {
                    Send(client, AgwpeFrame.Create('C', port, remote, own,
                        Encoding.ASCII.GetBytes($"*** CONNECTED To Station {remote}\r")));
                }
            };
        }
        else
        {
            // Inbound notification form (no "Station").
            Send(client, AgwpeFrame.Create('C', port, remote, own,
                Encoding.ASCII.GetBytes($"*** CONNECTED To {remote}\r")));
        }

        session.Closed += (_, _) =>
        {
            lock (client.StateLock)
                client.Connections.Remove((port, own, remote));
            Send(client, AgwpeFrame.Create('d', port, remote, own,
                Encoding.ASCII.GetBytes($"*** DISCONNECTED From Station {remote}\r")));
        };

        _ = Task.Run(async () =>
        {
            await foreach (var data in session.Received.ReadAllAsync())
                Send(client, AgwpeFrame.Create('D', port, remote, own, data, pid: 0xF0));
        });
    }

    private sealed class ClientInboundHandler(AgwpeTcpServerService server, AgwpeClient client)
        : IAx25InboundHandler
    {
        public Task HandleSessionAsync(IAx25Session session, CancellationToken ct)
        {
            server.BindSession(client, session, connectNotifOnConnected: false);
            return Task.CompletedTask;
        }
    }

    // ------------------------------------------------------------------ util

    private void Send(AgwpeClient client, AgwpeFrame frame)
    {
        try
        {
            var bytes = AgwpeCodec.Encode(frame);
            lock (client.WriteLock)
                client.Tcp.GetStream().Write(bytes);
        }
        catch
        {
            DropClient(client);
        }
    }

    private IAx25Session? FindConnection(AgwpeClient client, byte port, string own, string remote)
    {
        lock (client.StateLock)
        {
            return client.Connections.GetValueOrDefault(
                (port, own.ToUpperInvariant(), remote.ToUpperInvariant()));
        }
    }

    private int CountConnections(AgwpeClient client, byte port)
    {
        lock (client.StateLock)
            return client.Connections.Count(kv => kv.Key.Port == port);
    }

    private void DropClient(AgwpeClient client)
    {
        lock (_clientsLock)
        {
            if (!_clients.Remove(client))
                return;
        }
        CleanUpClient(client);
    }

    private void CleanUpClient(AgwpeClient client)
    {
        List<string> callsigns;
        List<IAx25Session> sessions;
        lock (client.StateLock)
        {
            callsigns = [.. client.RegisteredCallsigns];
            client.RegisteredCallsigns.Clear();
            sessions = [.. client.Connections.Values];
            client.Connections.Clear();
        }
        foreach (var callsign in callsigns)
            sessionManager.UnregisterListener(callsign);
        foreach (var session in sessions)
            session.Abort();
        client.Tcp.Dispose();
    }

    private static byte[] BuildRawPayload(byte port, byte[] ax25Frame)
    {
        var payload = new byte[ax25Frame.Length + 1];
        payload[0] = port;
        ax25Frame.CopyTo(payload, 1);
        return payload;
    }

    /// <summary>Data layout shared by 'V' and 'v': 1-byte digi count, 10 bytes per digi, then payload.</summary>
    private static (List<string> Path, byte[] Info) ParseViaPayload(byte[] data)
    {
        if (data.Length == 0)
            return ([], []);

        var count = Math.Min((int)data[0], 8);
        var path = new List<string>(count);
        var offset = 1;
        for (var i = 0; i < count && offset + 10 <= data.Length; i++, offset += 10)
        {
            var span = data.AsSpan(offset, 10);
            var end = span.IndexOf((byte)0);
            var call = Encoding.ASCII.GetString(end < 0 ? span : span[..end]).Trim();
            if (call.Length > 0)
                path.Add(call);
        }
        return (path, data[Math.Min(offset, data.Length)..]);
    }

    private byte PortForChannel(int channel)
    {
        var ports = _ports;
        for (var i = 0; i < ports.Count; i++)
        {
            if (ports[i].Channel == channel)
                return (byte)i;
        }
        return 0;
    }

    private int ChannelForPort(byte port)
    {
        var ports = _ports;
        return port < ports.Count ? ports[port].Channel : port;
    }

    private Radio? PortRadio(byte port)
    {
        var ports = _ports;
        return port < ports.Count ? ports[port].Radio : null;
    }

    private async Task<List<PortInfo>> LoadPortsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        var radios = await db.Radios
            .AsNoTracking()
            .Where(r => r.IsActive)
            .OrderBy(r => r.ChannelNumber)
            .ToListAsync(ct);
        return radios
            .Select(r => new PortInfo(r.ChannelNumber, $"{r.Name} ({r.FullCallsign})", r))
            .ToList();
    }

    private async Task<UserSetting?> GetSettingsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        return await db.UserSettings.FindAsync([1], ct);
    }
}
