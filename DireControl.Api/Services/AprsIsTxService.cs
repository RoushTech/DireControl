using System.Net.Sockets;
using System.Text;
using DireControl.Data;
using DireControl.Data.Models;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Send-only APRS-IS connection that drains the RF→IS gate queue
/// (<see cref="AprsIsTxQueue"/>).  Kept separate from the receive client —
/// the AprsSharp client exposes no public send path — and logged in without a
/// filter so the server pushes (almost) nothing back.  Standard practice for
/// transmit-only igate/tracker connections.
/// </summary>
public sealed class AprsIsTxService(
    IServiceScopeFactory scopeFactory,
    AprsIsTxQueue txQueue,
    AprsIsReconnectTrigger reconnectTrigger,
    IOptions<DireControlOptions> options,
    ILogger<AprsIsTxService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var iterCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, reconnectTrigger.Token);
            var ct = iterCts.Token;

            try
            {
                // Wait for something to send before holding a connection open.
                if (!await txQueue.Reader.WaitToReadAsync(ct))
                    break;

                var settings = await GetSettingsAsync(stoppingToken);
                if (settings is not { AprsIsEnabled: true, RfToIsGatingEnabled: true })
                {
                    // Gating switched off with lines still queued — drop them.
                    while (txQueue.Reader.TryRead(out _)) { }
                    continue;
                }

                await ConnectAndSendAsync(settings, ct);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                continue; // settings changed — reconnect with new values
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "APRS-IS TX connection failed; retrying in {Delay}s.", RetryDelay.TotalSeconds);
                try { await Task.Delay(RetryDelay, ct); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { }
            }
        }

        logger.LogInformation("AprsIsTxService stopped.");
    }

    private async Task ConnectAndSendAsync(UserSetting settings, CancellationToken ct)
    {
        var callsign = options.Value.OurCallsign.Trim().ToUpperInvariant();
        var passcode = settings.AprsIsPasscode ?? AprsPasscodeHelper.GeneratePasscode(callsign);

        logger.LogInformation("Opening APRS-IS TX connection to {Host}:{Port}…", settings.AprsIsHost, settings.AprsIsPort);

        using var client = new TcpClient();
        await client.ConnectAsync(settings.AprsIsHost, settings.AprsIsPort, ct);
        var stream = client.GetStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Send-only login: no filter, so the server pushes nothing back.
        await writer.WriteLineAsync($"user {callsign} pass {passcode} vers DireControl 0.3");

        // Read and discard server chatter in the background; a completed read
        // task means the server closed the connection.
        var readTask = Task.Run(async () =>
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Contains("unverified", StringComparison.OrdinalIgnoreCase))
                    logger.LogError("APRS-IS TX login unverified — RF→IS gating will be ignored by the server.");
            }
        }, ct);

        logger.LogInformation("APRS-IS TX connection ready.");

        while (!ct.IsCancellationRequested)
        {
            var waitToRead = txQueue.Reader.WaitToReadAsync(ct).AsTask();
            var completed = await Task.WhenAny(waitToRead, readTask);
            if (completed == readTask)
            {
                await readTask; // propagate any error
                throw new EndOfStreamException("APRS-IS TX connection closed by server.");
            }

            if (!await waitToRead)
                return;

            while (txQueue.Reader.TryRead(out var line))
            {
                await writer.WriteLineAsync(line.AsMemory(), ct);
                txQueue.RecordSent();
                logger.LogDebug("Gated RF→IS: {Line}", line);
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task<UserSetting?> GetSettingsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        return await db.UserSettings.FindAsync([1], ct);
    }
}
