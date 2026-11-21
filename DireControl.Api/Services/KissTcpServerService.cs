using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using DireControl.Data;
using DireControl.Modem.Kiss;

namespace DireControl.Api.Services;

/// <summary>
/// KISS TCP <em>server</em>: lets external applications (APRS clients, packet
/// software) use DireControl as their TNC.  Every RF-decoded frame is
/// broadcast to connected clients as a KISS data frame, and data frames sent
/// by clients are transmitted over RF via the shared <see cref="IFrameTransmitter"/>.
/// Restarts on the <c>KissServer</c> restart signal so settings changes apply live.
/// </summary>
public sealed class KissTcpServerService(
    IServiceScopeFactory scopeFactory,
    IFrameTransmitter transmitter,
    [FromKeyedServices(RestartSignals.KissServer)] RestartSignal restartTrigger,
    ILogger<KissTcpServerService> logger) : BackgroundService
{
    /// <summary>
    /// Frames buffered per client before the oldest are dropped.  A client that
    /// stops reading only loses its own backlog — it never blocks ingest.
    /// </summary>
    internal const int ClientQueueCapacity = 256;

    /// <summary>A write that cannot complete in this long means the client is dead.</summary>
    private static readonly TimeSpan ClientWriteTimeout = TimeSpan.FromSeconds(10);

    private sealed class KissClient(TcpClient tcp)
    {
        public TcpClient Tcp { get; } = tcp;
        public Channel<byte[]> Outbound { get; } = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(ClientQueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
    }

    private readonly Lock _clientsLock = new();
    private readonly List<KissClient> _clients = [];

    /// <summary>Currently connected KISS clients.</summary>
    public int ClientCount
    {
        get { lock (_clientsLock) return _clients.Count; }
    }

    /// <summary>
    /// Sends an RF-received frame to every connected client.  Never blocks:
    /// each client has its own bounded queue drained by its own writer task,
    /// so a client that stops reading cannot stall the ingest path.
    /// </summary>
    public void Broadcast(byte[] ax25Frame, int channel)
    {
        List<KissClient> clients;
        lock (_clientsLock)
        {
            if (_clients.Count == 0)
                return;
            clients = [.. _clients];
        }

        var kissFrame = KissCodec.EncodeDataFrame(channel, ax25Frame);
        foreach (var client in clients)
            client.Outbound.Writer.TryWrite(kissFrame);
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
                if (settings is not { KissServerEnabled: true })
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    continue;
                }

                var bind = IPAddress.TryParse(settings.KissServerBindAddress, out var ip)
                    ? ip
                    : IPAddress.Any;
                await ListenAsync(bind, settings.KissServerPort, ct);
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
                logger.LogError(ex, "KISS server error; retrying in 10 s.");
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { }
            }
        }

        logger.LogInformation("KissTcpServerService stopped.");
    }

    private async Task ListenAsync(IPAddress bind, int port, CancellationToken ct)
    {
        var listener = new TcpListener(bind, port);
        listener.Start();
        logger.LogInformation("KISS TCP server listening on {Bind}:{Port}.", bind, port);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                var client = new KissClient(tcp);
                lock (_clientsLock)
                    _clients.Add(client);

                logger.LogInformation(
                    "KISS client connected from {Remote} ({Count} total).",
                    tcp.Client.RemoteEndPoint, ClientCount);

                _ = Task.Run(() => ServeClientAsync(client, ct), ct);
                _ = Task.Run(() => WriteClientAsync(client, ct), ct);
            }
        }
        finally
        {
            listener.Stop();
            lock (_clientsLock)
            {
                foreach (var client in _clients)
                {
                    client.Outbound.Writer.TryComplete();
                    client.Tcp.Dispose();
                }
                _clients.Clear();
            }
        }
    }

    private async Task WriteClientAsync(KissClient client, CancellationToken ct)
    {
        try
        {
            var stream = client.Tcp.GetStream();
            await foreach (var frame in client.Outbound.Reader.ReadAllAsync(ct))
            {
                using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                writeCts.CancelAfter(ClientWriteTimeout);
                await stream.WriteAsync(frame, writeCts.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException
            or OperationCanceledException or InvalidOperationException)
        {
            // client gone, stuck past the write timeout, or shutdown
        }
        finally
        {
            DropClient(client);
        }
    }

    private async Task ServeClientAsync(KissClient kissClient, CancellationToken ct)
    {
        var client = kissClient.Tcp;
        var decoder = new KissDecoder();
        decoder.FrameReceived += (command, channel, payload) =>
        {
            // Only data frames transmit; TXDELAY/persistence/etc. are accepted
            // and ignored — channel timing is DireControl's own configuration.
            // The client's channel nibble routes to the matching radio.
            if (command != KissCodec.DataFrameCommand || payload.Length == 0)
                return;

            if (transmitter.TrySend(payload, channel))
                logger.LogInformation("KISS client frame queued for TX ({Bytes} bytes).", payload.Length);
            else
                logger.LogWarning("KISS client frame dropped: no RF transmit backend available.");
        };

        var buffer = new byte[4096];
        try
        {
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                    break;
                decoder.ProcessBytes(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // client gone or shutdown — normal
        }
        finally
        {
            DropClient(kissClient);
        }
    }

    private void DropClient(KissClient client)
    {
        bool removed;
        lock (_clientsLock)
            removed = _clients.Remove(client);
        client.Outbound.Writer.TryComplete();
        client.Tcp.Dispose();
        if (removed)
            logger.LogInformation("KISS client disconnected ({Count} remaining).", ClientCount);
    }

    private async Task<Data.Models.UserSetting?> GetSettingsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        return await db.UserSettings.FindAsync([1], ct);
    }
}
