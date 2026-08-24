using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using DireControl.Modem.Ax25;
using DireControl.PathParsing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Services;

/// <summary>
/// Shared ingest path for RF-received AX.25 frames, regardless of backend
/// (external KISS TNC or the native sound modem): decodes the frame to TNC2,
/// validates it as APRS, deduplicates against APRS-IS copies, upserts the
/// station, and persists the raw packet for the parsing pipeline.
/// </summary>
public sealed class RfFrameIngestService(
    IServiceScopeFactory scopeFactory,
    DigipeaterService digipeaterService,
    KissTcpServerService kissServer,
    AgwpeTcpServerService agwpeServer,
    Ax25.Ax25SessionManager sessionManager,
    AprsIsTxQueue aprsIsTxQueue,
    Microsoft.AspNetCore.SignalR.IHubContext<Hubs.PacketHub> hubContext,
    IOptions<DireControlOptions> options,
    ILogger<RfFrameIngestService> logger)
{
    /// <summary>
    /// Ingests one raw AX.25 UI frame (no flags/FCS).  Returns the stored
    /// packet id, or <see langword="null"/> when the frame was dropped
    /// (malformed / non-APRS) or deduplicated into an existing APRS-IS row.
    /// </summary>
    /// <param name="signalData">
    /// Demodulator quality metadata — populated by the native modem, always
    /// null for external KISS TNCs (standard KISS carries no signal info).
    /// </param>
    /// <param name="isOwnTransmission">
    /// True for the loopback ingest of frames we transmitted ourselves —
    /// still stored and gated to APRS-IS, but never digipeated.
    /// </param>
    public async Task<int?> IngestAsync(
        byte[] ax25Frame,
        int kissChannel,
        SignalData? signalData,
        CancellationToken ct,
        bool isOwnTransmission = false)
    {
        // Decode the raw AX.25 frame to TNC2 format first, preserving the
        // has-been-repeated (H) bit on each repeater address as a '*' suffix.
        // This must happen before any APRSSharp parsing so the asterisks are
        // never lost to APRSSharp's EncodeTnc2() which does not round-trip them.
        if (!Ax25Decoder.TryDecode(ax25Frame, out var frame))
        {
            logger.LogTrace("Dropped malformed/non-APRS frame ({Bytes} bytes).", ax25Frame.Length);
            return null;
        }

        // Serve raw AX.25 to connected KISS and AGWPE clients — they get every
        // decodable frame, not just what the APRS pipeline accepts.
        kissServer.Broadcast(ax25Frame, kissChannel);
        agwpeServer.Broadcast(ax25Frame, kissChannel, isOwnTransmission);

        // Connected-mode (LAPB) tap: sessions and inbound listeners see every
        // non-UI frame addressed to them. Consumed frames are non-APRS by
        // definition — stop here, after the KISS broadcast so external stacks
        // still hear everything. Own-transmission loopbacks never re-enter the
        // session layer (it would ack its own frames).
        if (!isOwnTransmission && sessionManager.OfferFrame(ax25Frame, frame, kissChannel))
            return null;

        // WIDEn-N digipeating (never our own transmissions).
        if (!isOwnTransmission)
        {
            _ = digipeaterService.ConsiderAsync(frame, kissChannel, ct).ContinueWith(
                t => logger.LogError(t.Exception, "Unhandled digipeater error."),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        var rawPacket = frame.ToTnc2();

        // APRSSharp is still used only to validate the frame and extract the sender.
        AprsSharp.AprsParser.Packet aprsPacket;
        try
        {
            aprsPacket = new AprsSharp.AprsParser.Packet(ax25Frame);
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "Dropped malformed/non-APRS frame ({Bytes} bytes).", ax25Frame.Length);
            return null;
        }

        var callsign = aprsPacket.Sender;
        if (string.IsNullOrWhiteSpace(callsign))
        {
            logger.LogTrace("Dropped frame with empty callsign.");
            return null;
        }

        var colonIdx = rawPacket.IndexOf(':');
        var infoField = colonIdx >= 0 ? rawPacket[(colonIdx + 1)..] : string.Empty;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        // Load dedup window
        var setting = await db.UserSettings.FindAsync([1], ct);
        var dedupWindowSeconds = setting?.DeduplicationWindowSeconds ?? 60;

        // RF→IS gating: every RF-heard APRS packet (including our own beacons)
        // is queued for APRS-IS with the qAR construct appended.
        if (setting is { AprsIsEnabled: true, RfToIsGatingEnabled: true } &&
            IgateLogic.BuildRfToIsLine(rawPacket, options.Value.OurCallsign) is { } gateLine)
        {
            aprsIsTxQueue.Enqueue(gateLine);
        }
        var dedupWindow = DateTime.UtcNow.AddSeconds(-dedupWindowSeconds);

        // If an APRS-IS copy arrived first, upgrade it to RF rather than inserting a duplicate.
        var existingAprsIs = await db.Packets
            .Where(p =>
                p.StationCallsign == callsign &&
                p.InfoField == infoField &&
                p.Source == PacketSource.AprsIs &&
                p.ReceivedAt >= dedupWindow)
            .OrderByDescending(p => p.ReceivedAt)
            .FirstOrDefaultAsync(ct);

        if (existingAprsIs is not null)
        {
            existingAprsIs.Source = PacketSource.Rf;
            existingAprsIs.KissChannel = kissChannel;
            existingAprsIs.SignalData ??= signalData;

            // Classify from the frame we actually heard, not from the stored APRS-IS raw
            // text — that copy's path carries qAR/TCPIP and would report our own direct
            // reception as igated, hiding it from the direct-RF statistics.
            var (_, _, rfPath) = AprsPathParser.ParseTnc2Header(rawPacket);
            existingAprsIs.HeardVia = AprsPathParser.ClassifyHeardVia(
                string.IsNullOrEmpty(rfPath)
                    ? []
                    : rfPath.Split(',', StringSplitOptions.RemoveEmptyEntries));
            var rfStation = await db.Stations.FindAsync([callsign], ct);
            if (rfStation is not null)
            {
                rfStation.LastSeen = DateTime.UtcNow;
                rfStation.LastHeardRf = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);

            // Tell the live views the packet was actually heard on RF — without
            // this the stream shows it as an APRS-IS packet forever and the
            // radio appears "deaf" whenever the internet copy arrives first.
            await hubContext.Clients.All.SendAsync(
                Hubs.PacketHub.PacketSourceUpgradedMethod,
                new { Id = existingAprsIs.Id, Source = PacketSource.Rf },
                ct);

            logger.LogDebug(
                "RF upgrade: APRS-IS packet id={Id} from {Callsign} upgraded to RF.",
                existingAprsIs.Id, callsign);
            return null;
        }

        var station = await db.Stations.FindAsync([callsign], ct);
        if (station is null)
        {
            db.Stations.Add(new Station
            {
                Callsign = callsign,
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                LastHeardRf = DateTime.UtcNow,
                Symbol = "/-",
            });
        }
        else
        {
            station.LastSeen = DateTime.UtcNow;
            station.LastHeardRf = DateTime.UtcNow;
        }

        var packet = new Packet
        {
            StationCallsign = callsign,
            ReceivedAt = DateTime.UtcNow,
            RawPacket = rawPacket,
            Source = PacketSource.Rf,
            InfoField = infoField,
            KissChannel = kissChannel,
            SignalData = signalData,
        };
        db.Packets.Add(packet);

        await db.SaveChangesAsync(ct);

        logger.LogDebug("Stored RF packet from {Callsign}: {RawPacket}", callsign, rawPacket);
        return packet.Id;
    }
}
