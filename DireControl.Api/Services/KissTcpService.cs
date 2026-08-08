using AprsSharp.KissTnc;
using AprsSharp.Shared;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Long-running service that maintains a KISS TCP connection to an external
/// TNC (Direwolf or hardware) via AprsSharp.KissTnc, receives AX.25 UI frames,
/// and hands them to the shared <see cref="RfFrameIngestService"/>.
/// Reconnects automatically.  Disabled entirely when
/// <see cref="DirewolfOptions.Enabled"/> is false (native sound modem only).
/// </summary>
public sealed class KissTcpService(
    IOptions<DirewolfOptions> options,
    RfFrameIngestService ingestService,
    KissConnectionHolder connectionHolder,
    ILogger<KissTcpService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("KISS TCP backend disabled (Direwolf:Enabled = false).");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReadAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Direwolf connection lost. Reconnecting in {Delay}s…",
                    options.Value.ReconnectDelaySeconds);

                await Task.Delay(
                    TimeSpan.FromSeconds(options.Value.ReconnectDelaySeconds),
                    stoppingToken);
            }
        }

        logger.LogInformation("KissTcpService stopped.");
    }

    private async Task ConnectAndReadAsync(CancellationToken ct)
    {
        using var tcpConnection = new TcpConnection();

        logger.LogInformation(
            "Connecting to Direwolf at {Host}:{Port}…",
            options.Value.Host, options.Value.Port);

        tcpConnection.Connect(options.Value.Host, options.Value.Port);
        logger.LogInformation("Connected to Direwolf.");

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
                throw new EndOfStreamException("Direwolf closed the connection.");
        }
        finally
        {
            connectionHolder.SetTnc(null);
        }
    }
}
