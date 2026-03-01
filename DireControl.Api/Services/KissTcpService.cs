using AprsSharp.KissTnc;
using AprsSharp.Shared;
using DireControl.Data;
using DireControl.Data.Models;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Long-running service that maintains a KISS TCP connection to Direwolf via
/// AprsSharp.KissTnc, receives AX.25 UI frames as APRS packets, and persists
/// the raw TNC2 string to the database.  Reconnects automatically.
/// </summary>
public sealed class KissTcpService(
    IOptions<DirewolfOptions> options,
    IServiceScopeFactory scopeFactory,
    KissConnectionHolder connectionHolder,
    ILogger<KissTcpService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

        using var tnc = new TcpTnc(tcpConnection, 0);
        connectionHolder.SetTnc(tnc);

        try
        {
            tnc.FrameReceivedEvent += (_, e) =>
            {
                var data = e.Data;
                _ = ProcessFrameAsync(data, ct).ContinueWith(
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

    private async Task ProcessFrameAsync(IReadOnlyList<byte> data, CancellationToken ct)
    {
        AprsSharp.AprsParser.Packet aprsPacket;
        try
        {
            aprsPacket = new AprsSharp.AprsParser.Packet(data.ToArray());
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "Dropped malformed/non-APRS frame ({Bytes} bytes).", data.Count);
            return;
        }

        var callsign = aprsPacket.Sender;
        if (string.IsNullOrWhiteSpace(callsign))
        {
            logger.LogTrace("Dropped frame with empty callsign.");
            return;
        }

        string rawPacket;
        if (aprsPacket.InfoField is AprsSharp.AprsParser.UnsupportedInfo unsupported)
        {
            logger.LogDebug("Unsupported packet type from {Callsign} — storing raw.", callsign);
            var pathStr = aprsPacket.Path is { Count: > 0 } ? "," + string.Join(",", aprsPacket.Path) : "";
            rawPacket = $"{aprsPacket.Sender}>{aprsPacket.Destination}{pathStr}:{unsupported.Content}";
        }
        else
        {
            rawPacket = aprsPacket.EncodeTnc2();
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        var station = await db.Stations.FindAsync(new object[] { callsign }, ct);
        if (station is null)
        {
            db.Stations.Add(new Station
            {
                Callsign = callsign,
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                Symbol = "/-",
            });
        }
        else
        {
            station.LastSeen = DateTime.UtcNow;
        }

        db.Packets.Add(new Packet
        {
            StationCallsign = callsign,
            ReceivedAt = DateTime.UtcNow,
            RawPacket = rawPacket,
            // Direwolf's standard KISS TCP interface carries only the raw AX.25 frame
            // bytes — there is no signal-quality metadata (decode quality, frequency
            // offset, audio level) in the KISS frame header.  The AGW/AGWPE interface
            // does expose some additional per-frame fields, but this implementation uses
            // KISS TCP.  SignalData is therefore always null for frames received here.
        });

        await db.SaveChangesAsync(ct);

        logger.LogDebug("Stored packet from {Callsign}: {RawPacket}", callsign, rawPacket);
    }
}
