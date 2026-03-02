using System.Text;
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

        const byte tncPort = 0;
        using var tnc = new TcpTnc(tcpConnection, tncPort);
        connectionHolder.SetTnc(tnc);

        try
        {
            tnc.FrameReceivedEvent += (_, e) =>
            {
                var data = e.Data;
                _ = ProcessFrameAsync(data, tncPort, ct).ContinueWith(
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

    private async Task ProcessFrameAsync(IReadOnlyList<byte> data, int kissChannel, CancellationToken ct)
    {
        // Decode the raw AX.25 frame to TNC2 format first, preserving the
        // has-been-repeated (H) bit on each repeater address as a '*' suffix.
        // This must happen before any APRSSharp parsing so the asterisks are
        // never lost to APRSSharp's EncodeTnc2() which does not round-trip them.
        var rawPacket = DecodeAx25ToTnc2(data);
        if (string.IsNullOrEmpty(rawPacket))
        {
            logger.LogTrace("Dropped malformed/non-APRS frame ({Bytes} bytes).", data.Count);
            return;
        }

        // APRSSharp is still used only to validate the frame and extract the sender.
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
            KissChannel = kissChannel,
            // Direwolf's standard KISS TCP interface carries only the raw AX.25 frame
            // bytes — there is no signal-quality metadata (decode quality, frequency
            // offset, audio level) in the KISS frame header.  The AGW/AGWPE interface
            // does expose some additional per-frame fields, but this implementation uses
            // KISS TCP.  SignalData is therefore always null for frames received here.
        });

        await db.SaveChangesAsync(ct);

        logger.LogDebug("Stored packet from {Callsign}: {RawPacket}", callsign, rawPacket);
    }

    /// <summary>
    /// Decodes a raw AX.25 UI frame (as delivered by KISS) into a TNC2-format string.
    /// The AX.25 has-been-repeated (H) bit in each repeater address is emitted as a
    /// <c>*</c> suffix, preserving the digipeater trail that <c>EncodeTnc2()</c> discards.
    /// Returns <see cref="string.Empty"/> when the frame is too short to be a valid UI frame.
    /// </summary>
    private static string DecodeAx25ToTnc2(IReadOnlyList<byte> data)
    {
        // AX.25 UI frame layout (after KISS framing stripped):
        //   [0..6]   Destination (TOCALL)  — 7 bytes
        //   [7..13]  Source               — 7 bytes
        //   [14..]   Repeaters            — 7 bytes each; presence determined by end-bit
        //   Control byte (0x03 for UI)
        //   PID byte  (0xF0 for APRS)
        //   Information field (rest)
        //
        // Each address byte layout (byte 6 = SSID byte):
        //   Bit 7 : H (has-been-repeated) — meaningful only for repeater entries
        //   Bits 4–1 : SSID value 0–15
        //   Bit 0 : end-of-address-list flag

        // Minimum: 7 (dest) + 7 (src) + 1 (ctrl) + 1 (pid) = 16 bytes
        if (data.Count < 16)
            return string.Empty;

        static (string Call, int Ssid, bool HBit, bool EndBit) DecodeAddr(IReadOnlyList<byte> buf, int offset)
        {
            var chars = new char[6];
            for (var i = 0; i < 6; i++)
                chars[i] = (char)(buf[offset + i] >> 1);
            var call     = new string(chars).TrimEnd();
            var ssidByte = buf[offset + 6];
            var ssid     = (ssidByte >> 1) & 0x0F;
            var hBit     = (ssidByte & 0x80) != 0;
            var endBit   = (ssidByte & 0x01) != 0;
            return (call, ssid, hBit, endBit);
        }

        static string FormatCall(string call, int ssid, bool star = false)
        {
            var s = ssid > 0 ? $"{call}-{ssid}" : call;
            return star ? s + "*" : s;
        }

        var (destCall, destSsid, _, _)    = DecodeAddr(data, 0);
        var tocall                         = FormatCall(destCall, destSsid);

        var (srcCall, srcSsid, _, srcEnd) = DecodeAddr(data, 7);
        var source                         = FormatCall(srcCall, srcSsid);

        var viaList = new List<string>();
        var pos     = 14;
        var endBit  = srcEnd;

        while (!endBit && pos + 7 <= data.Count)
        {
            var (repCall, repSsid, hBit, rEnd) = DecodeAddr(data, pos);
            viaList.Add(FormatCall(repCall, repSsid, hBit));
            endBit = rEnd;
            pos   += 7;
        }

        // Skip control (1 byte) and PID (1 byte) to reach the info field.
        pos += 2;
        if (pos > data.Count)
            return string.Empty;

        var info     = Encoding.ASCII.GetString(data.ToArray(), pos, data.Count - pos);
        var pathPart = viaList.Count > 0 ? "," + string.Join(",", viaList) : string.Empty;

        return $"{source}>{tocall}{pathPart}:{info}";
    }
}
