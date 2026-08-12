using System.Net.WebSockets;
using System.Text;

namespace DireControl.Api.Services.Weather;

/// <summary>
/// Maintains a WebSocket connection to the Blitzortung.org community lightning network
/// and feeds decoded strikes into <see cref="LightningStrikeBuffer"/>. Rotates between
/// public feed hosts with exponential backoff on failure.
/// </summary>
public sealed class BlitzortungService(
    LightningStrikeBuffer buffer,
    ILogger<BlitzortungService> logger) : BackgroundService
{
    private static readonly string[] Hosts =
        ["ws1.blitzortung.org", "ws3.blitzortung.org", "ws7.blitzortung.org", "ws8.blitzortung.org"];

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hostIndex = 0;
        var backoff = InitialBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            var host = Hosts[hostIndex++ % Hosts.Length];
            var received = 0L;
            try
            {
                received = await RunConnectionAsync(host, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Blitzortung connection to {Host} failed", host);
            }
            finally
            {
                buffer.IsConnected = false;
            }

            // A connection that actually delivered strikes earns a fresh backoff.
            backoff = received > 0 ? InitialBackoff : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            await Task.Delay(backoff, stoppingToken);
        }
    }

    private async Task<long> RunConnectionAsync(string host, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "https://map.blitzortung.org");
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        await ws.ConnectAsync(new Uri($"wss://{host}/"), ct);
        await ws.SendAsync("{\"a\":111}"u8.ToArray(), WebSocketMessageType.Text, true, ct);

        buffer.IsConnected = true;
        logger.LogInformation("Connected to Blitzortung lightning feed at {Host}", host);

        var received = 0L;
        var chunk = new byte[16 * 1024];
        var message = new MemoryStream();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(chunk, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogInformation("Blitzortung feed {Host} closed the connection", host);
                    return received;
                }
                message.Write(chunk, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            var raw = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            if (BlitzortungDecoder.DecodeStrike(raw) is { } strike)
            {
                buffer.Add(strike);
                received++;
            }
        }

        return received;
    }
}
