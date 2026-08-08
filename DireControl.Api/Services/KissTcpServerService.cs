using System.Net;
using System.Net.Sockets;
using DireControl.Data;
using DireControl.Modem.Kiss;

namespace DireControl.Api.Services;

/// <summary>
/// KISS TCP <em>server</em>: lets external applications (APRS clients, packet
/// software) use DireControl as their TNC.  Every RF-decoded frame is
/// broadcast to connected clients as a KISS data frame, and data frames sent
/// by clients are transmitted over RF via the shared <see cref="IFrameTransmitter"/>.
/// Restarts on <see cref="ModemRestartTrigger"/> so settings changes apply live.
/// </summary>
public sealed class KissTcpServerService(
    IServiceScopeFactory scopeFactory,
    IFrameTransmitter transmitter,
    ModemRestartTrigger restartTrigger,
    ILogger<KissTcpServerService> logger) : BackgroundService
{
    private readonly Lock _clientsLock = new();
    private readonly List<TcpClient> _clients = [];

    /// <summary>Currently connected KISS clients.</summary>
    public int ClientCount
    {
        get { lock (_clientsLock) return _clients.Count; }
    }

    /// <summary>
    /// Sends an RF-received frame to every connected client.  Fire-and-forget;
    /// slow or dead clients are dropped rather than blocking the ingest path.
    /// </summary>
    public void Broadcast(byte[] ax25Frame, int channel)
    {
        List<TcpClient> clients;
        lock (_clientsLock)
        {
            if (_clients.Count == 0)
                return;
            clients = [.. _clients];
        }

        var kissFrame = KissCodec.EncodeDataFrame(channel, ax25Frame);
        foreach (var client in clients)
        {
            try
            {
                client.GetStream().Write(kissFrame);
            }
            catch
            {
                DropClient(client);
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
                if (settings is not { KissServerEnabled: true })
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    continue;
                }

                await ListenAsync(settings.KissServerPort, ct);
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

    private async Task ListenAsync(int port, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        logger.LogInformation("KISS TCP server listening on port {Port}.", port);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                lock (_clientsLock)
                    _clients.Add(client);

                logger.LogInformation(
                    "KISS client connected from {Remote} ({Count} total).",
                    client.Client.RemoteEndPoint, ClientCount);

                _ = Task.Run(() => ServeClientAsync(client, ct), ct);
            }
        }
        finally
        {
            listener.Stop();
            lock (_clientsLock)
            {
                foreach (var client in _clients)
                    client.Dispose();
                _clients.Clear();
            }
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken ct)
    {
        var decoder = new KissDecoder();
        decoder.FrameReceived += (command, _, payload) =>
        {
            // Only data frames transmit; TXDELAY/persistence/etc. are accepted
            // and ignored — channel timing is DireControl's own configuration.
            if (command != KissCodec.DataFrameCommand || payload.Length == 0)
                return;

            if (transmitter.TrySend(payload))
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
            DropClient(client);
            logger.LogInformation("KISS client disconnected ({Count} remaining).", ClientCount);
        }
    }

    private void DropClient(TcpClient client)
    {
        lock (_clientsLock)
            _clients.Remove(client);
        client.Dispose();
    }

    private async Task<Data.Models.UserSetting?> GetSettingsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        return await db.UserSettings.FindAsync([1], ct);
    }
}
