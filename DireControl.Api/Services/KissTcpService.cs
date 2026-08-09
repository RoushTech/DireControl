using AprsSharp.KissTnc;
using AprsSharp.Shared;
using DireControl.Data;
using DireControl.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Services;

/// <summary>
/// Long-running service that maintains a KISS TCP connection to an external
/// TNC (Direwolf or hardware) via AprsSharp.KissTnc, receives AX.25 UI frames,
/// and hands them to the shared <see cref="RfFrameIngestService"/>.
/// Reconnects automatically.  Enabled and configured from the DB-backed
/// <see cref="UserSetting"/> (External TNC settings), and re-reads them whenever
/// <see cref="KissReconnectTrigger"/> fires.
/// </summary>
public sealed class KissTcpService(
    IServiceScopeFactory scopeFactory,
    RfFrameIngestService ingestService,
    KissConnectionHolder connectionHolder,
    KissReconnectTrigger reconnectTrigger,
    ILogger<KissTcpService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Per-iteration linked token so a settings change (KissReconnectTrigger)
            // tears down the current connection and re-reads configuration.
            using var iterCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, reconnectTrigger.Token);
            var ct = iterCts.Token;

            var settings = await LoadSettingsAsync(stoppingToken);

            try
            {
                if (!settings.DirewolfEnabled)
                {
                    logger.LogInformation(
                        "External TNC (KISS TCP) disabled — using the native sound modem only.");
                    // Sleep until a settings change wakes us to re-evaluate.
                    await Task.Delay(Timeout.Infinite, ct);
                    continue;
                }

                await ConnectAndReadAsync(settings, ct);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Settings changed — loop and re-read immediately.
                continue;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "External TNC connection lost. Reconnecting in {Delay}s…",
                    settings.DirewolfReconnectDelaySeconds);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(settings.DirewolfReconnectDelaySeconds), ct);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Settings changed during the backoff — re-read now.
                }
            }
        }

        logger.LogInformation("KissTcpService stopped.");
    }

    private async Task<UserSetting> LoadSettingsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();
        return await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct)
            ?? new UserSetting { Id = 1 };
    }

    private async Task ConnectAndReadAsync(UserSetting settings, CancellationToken ct)
    {
        using var tcpConnection = new TcpConnection();

        logger.LogInformation(
            "Connecting to external TNC at {Host}:{Port}…",
            settings.DirewolfHost, settings.DirewolfPort);

        tcpConnection.Connect(settings.DirewolfHost, settings.DirewolfPort);
        logger.LogInformation("Connected to external TNC.");

        const byte tncPort = 0;
        using var tnc = new TcpTnc(tcpConnection, tncPort);
        connectionHolder.SetTnc(tnc);

        try
        {
            tnc.FrameReceivedEvent += (_, e) =>
            {
                var data = e.Data.ToArray();
                _ = ingestService.IngestAsync(data, tncPort, signalData: null, ct).ContinueWith(
                    t => logger.LogError(t.Exception, "Unhandled error processing APRS frame."),
                    TaskContinuationOptions.OnlyOnFaulted);
            };

            // Poll until disconnected or cancelled
            while (!ct.IsCancellationRequested && tcpConnection.Connected)
                await Task.Delay(500, ct);

            if (!ct.IsCancellationRequested)
                throw new EndOfStreamException("External TNC closed the connection.");
        }
        finally
        {
            connectionHolder.SetTnc(null);
        }
    }
}
