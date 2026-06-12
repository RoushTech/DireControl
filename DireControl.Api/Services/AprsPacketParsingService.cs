using DireControl.Api.Controllers.Models;
using DireControl.Api.Hubs;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using DireControl.PathParsing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OurPacketType = DireControl.Enums.PacketType;
using DbPacket = DireControl.Data.Models.Packet;

namespace DireControl.Api.Services;

/// <summary>
/// Background service that processes raw TNC2 packets stored by <see cref="KissTcpService"/>
/// (where <see cref="Packet.ParsedType"/> is <see cref="OurPacketType.Unknown"/>) and
/// populates all structured fields using AprsSharp.AprsParser.
/// </summary>
public sealed class AprsPacketParsingService(
    IServiceScopeFactory scopeFactory,
    IHubContext<PacketHub> hubContext,
    StationSettingsProvider settingsProvider,
    MessageSendingService messageSendingService,
    PendingAlertChannel alertChannel,
    ILogger<AprsPacketParsingService> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private const int PollIntervalMs = 5_000;

    /// <summary>
    /// A station must have position packets spanning at least this many hours,
    /// all within <see cref="PacketDecoder.MovementThresholdDeg"/>, before it is classified Fixed.
    /// </summary>
    private const int FixedDetectionWindowHours = 4;

    /// <summary>
    /// Minimum number of position packets required within the Fixed detection window
    /// before the classification fires (avoids single-packet false positives).
    /// </summary>
    private const int FixedDetectionMinPackets = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in parsing batch.");
            }

            await Task.Delay(PollIntervalMs, stoppingToken);
        }

        logger.LogInformation("AprsPacketParsingService stopped.");
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DireControlContext>();

        var packets = await db.Packets
            .Where(p => p.ParsedType == OurPacketType.Unknown)
            .OrderBy(p => p.ReceivedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (packets.Count == 0)
            return;

        logger.LogDebug("Parsing batch of {Count} packets.", packets.Count);

        // Pre-load all stations this batch can touch — senders plus digipeater hops
        // from the raw path headers — so path resolution never queries per packet.
        var callsigns = packets.Select(p => p.StationCallsign).Distinct().ToList();
        var preloadCallsigns = callsigns
            .Concat(packets.SelectMany(HopCallsignsFromHeader))
            .Distinct()
            .ToList();
        await db.Stations.Where(s => preloadCallsigns.Contains(s.Callsign)).LoadAsync(ct);

        var settings = await settingsProvider.GetAsync(ct);
        var ourCallsign = settings.OurCallsign.Trim();

        // Pre-load active radios so own-beacon detection doesn't need per-packet DB queries.
        var activeRadios = await db.Radios.Where(r => r.IsActive).ToListAsync(ct);

        var parsed = new List<(DbPacket Packet, List<MessageEffect> Effects)>();
        foreach (var packet in packets)
        {
            var effects = new List<MessageEffect>();
            try
            {
                await PacketDecoder.DecodeAsync(packet, db, ourCallsign, effects, ct, activeRadios: activeRadios);
                await PacketDecoder.ResolvePathCoordinatesAsync(
                    packet, db, ourCallsign, settings.HomeLat, settings.HomeLon, ct);
                parsed.Add((packet, effects));
            }
            catch (Exception ex)
            {
                logger.LogTrace(ex, "Could not parse packet {Id} ({Raw}).", packet.Id, packet.RawPacket);
                // Revert this packet's fields and mark it Unparseable so it is not
                // re-queued. Parse failures throw inside AprsSharp before any station
                // mutation, so reverting the packet row alone is sufficient.
                var entry = db.Entry(packet);
                entry.CurrentValues.SetValues(entry.OriginalValues);
                packet.ParsedType = OurPacketType.Unparseable;
                packet.ParserVersion = ParserVersionInfo.Current;
            }
        }

        // One commit for the whole batch instead of one per packet.
        await db.SaveChangesAsync(ct);

        foreach (var (packet, effects) in parsed)
        {
            await CheckOwnBeaconAsync(packet, activeRadios, db, ct);
            await ApplyMessageEffectsAsync(effects, db, ourCallsign, ct);

            var update = new PacketBroadcastDto
            {
                Id = packet.Id,
                Callsign = packet.StationCallsign,
                ParsedType = packet.ParsedType.ToString(),
                Source = packet.Source,
                ReceivedAt = packet.ReceivedAt,
                Latitude = packet.Latitude,
                Longitude = packet.Longitude,
                Summary = PacketDecoder.BuildSummary(packet),
                HopCount = packet.HopCount,
                ResolvedPath = packet.ResolvedPath,
            };

            await hubContext.Clients.All.SendAsync(PacketHub.PacketReceivedMethod, update, ct);

            // Notify alerting service of updated station
            alertChannel.Writer.TryWrite(packet.StationCallsign);
        }

        await UpdateStationStatisticsAsync(db, packets, ct);
    }

    /// <summary>
    /// Extracts real digipeater callsigns from a packet's raw TNC2 header without
    /// fully parsing it, so the batch station preload can cover path-resolution lookups.
    /// </summary>
    private static IEnumerable<string> HopCallsignsFromHeader(DbPacket packet)
    {
        string rawPath;
        try
        {
            (_, _, rawPath) = AprsPathParser.ParseTnc2Header(packet.RawPacket);
        }
        catch
        {
            yield break;
        }

        foreach (var entry in rawPath.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var callsign = entry.TrimEnd('*');
            if (callsign.Length > 0
                && !AprsPathParser.IsGenericAlias(callsign)
                && !AprsPathParser.IsInternetToken(callsign))
            {
                yield return callsign;
            }
        }
    }

    /// <summary>
    /// Re-derives all of a packet's structured fields from its <c>RawPacket</c> without
    /// firing any of the reactive side effects of live parsing (inbox additions,
    /// auto-ACK/replies, station-type promotion, mode/frequency updates, statistics,
    /// own-beacon detection, hub broadcasts, alerts). Used by
    /// <see cref="PacketReprocessingService"/> to repair historical rows after a parser
    /// change. Stamps <see cref="DireControl.Data.Models.Packet.ParserVersion"/>.
    /// The caller is responsible for ensuring the derived <c>StationCallsign</c> has a
    /// backing <c>Station</c> row and for persisting the changes.
    /// </summary>
    public async Task ReprocessOneAsync(DbPacket packet, DireControlContext db, string ourCallsign, CancellationToken ct)
    {
        var settings = await settingsProvider.GetAsync(ct);
        var discardedEffects = new List<MessageEffect>();
        await PacketDecoder.DecodeAsync(packet, db, ourCallsign, discardedEffects, ct, reprocess: true);
        await PacketDecoder.ResolvePathCoordinatesAsync(
            packet, db, ourCallsign, settings.HomeLat, settings.HomeLon, ct, reprocess: true);
    }

    private async Task ApplyMessageEffectsAsync(
        List<MessageEffect> effects,
        DireControlContext db,
        string ourCallsign,
        CancellationToken ct)
    {
        foreach (var effect in effects)
        {
            if (effect.IsNewInboxMessage)
            {
                // Send auto-ACK (sourced from the callsign the message was addressed to)
                await messageSendingService.SendAckAsync(effect.PeerCallsign, effect.MessageId, effect.AddresseeCallsign, ct);

                var msg = db.Messages.Local.FirstOrDefault(m =>
                    m.FromCallsign == effect.PeerCallsign &&
                    m.MessageId == effect.MessageId &&
                    m.ToCallsign.Equals(ourCallsign, StringComparison.OrdinalIgnoreCase));

                if (msg is not null)
                {
                    msg.AckSent = true;
                    await db.SaveChangesAsync(ct);

                    await hubContext.Clients.All.SendAsync(
                        PacketHub.MessageReceivedMethod,
                        ToInboxDto(msg),
                        ct);
                }
            }
            else if (effect.IsAckReceived && effect.OriginalMsgId is not null)
            {
                // Mark our sent message as ACK'd and broadcast to the UI.
                var ackedId = await MessageHandlingLogic.TryApplyAckAsync(
                    effect.PeerCallsign, effect.OriginalMsgId, db, ourCallsign, ct);

                if (ackedId is int dbId)
                {
                    await hubContext.Clients.All.SendAsync(
                        PacketHub.MessageAckedMethod,
                        new MessageAckDto { Id = dbId, MessageId = effect.OriginalMsgId },
                        ct);
                }
            }
            else if (effect.IsDuplicateInboxMessage)
            {
                // Remote station retransmitted because our earlier ACK was lost.
                // Re-send the ACK so it stops retrying; no new inbox entry is created.
                await messageSendingService.SendAckAsync(effect.PeerCallsign, effect.MessageId, effect.AddresseeCallsign, ct);
            }
        }
    }

    private async Task UpdateStationStatisticsAsync(
        DireControlContext db,
        IReadOnlyList<DbPacket> batchPackets,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var todayStart = DateTime.Now.Date.ToUniversalTime();

        var byCallsign = batchPackets.GroupBy(p => p.StationCallsign).ToList();
        var batchCallsigns = byCallsign.Select(g => g.Key).ToList();

        // One query for all stats rows in the batch instead of one per callsign.
        var statsRows = await db.StationStatistics
            .Where(ss => batchCallsigns.Contains(ss.Callsign))
            .ToDictionaryAsync(ss => ss.Callsign, ct);

        foreach (var group in byCallsign)
        {
            var callsign = group.Key;
            try
            {
                var station = db.Stations.Local.FirstOrDefault(s => s.Callsign == callsign);
                if (station is null)
                    continue;

                statsRows.TryGetValue(callsign, out var existing);

                // Counters are maintained incrementally from the batch; a station's
                // history is COUNTed once ever (new row or pre-TotalPackets rows),
                // not re-counted every batch.
                int totalPackets;
                if (existing is null || existing.TotalPackets == 0)
                    totalPackets = await db.Packets
                        .CountAsync(p => p.StationCallsign == callsign, ct);
                else
                    totalPackets = existing.TotalPackets + group.Count();

                if (totalPackets == 0)
                    continue;

                // Day rollover (or first sighting today) re-seeds with a query bounded
                // to today; otherwise increment from the batch.
                int packetsToday;
                if (existing is null || existing.LastComputedAt < todayStart)
                    packetsToday = await db.Packets
                        .CountAsync(p => p.StationCallsign == callsign && p.ReceivedAt >= todayStart, ct);
                else
                    packetsToday = existing.PacketsToday + group.Count(p => p.ReceivedAt >= todayStart);

                var ageHours = Math.Max(1.0, (now - station.FirstSeen).TotalHours);
                var averagePerHour = totalPackets / ageHours;

                var longestGapMinutes = existing?.LongestGapMinutes ?? 0;

                var lastTwoTimes = await db.Packets
                    .Where(p => p.StationCallsign == callsign)
                    .OrderByDescending(p => p.ReceivedAt)
                    .Take(2)
                    .Select(p => p.ReceivedAt)
                    .ToListAsync(ct);

                if (lastTwoTimes.Count >= 2)
                {
                    var latestGap = (int)(lastTwoTimes[0] - lastTwoTimes[1]).TotalMinutes;
                    if (latestGap > longestGapMinutes)
                        longestGapMinutes = latestGap;
                }

                if (existing is null)
                {
                    db.StationStatistics.Add(new DireControl.Data.Models.StationStatistic
                    {
                        Callsign = callsign,
                        PacketsToday = packetsToday,
                        TotalPackets = totalPackets,
                        AveragePacketsPerHour = averagePerHour,
                        LongestGapMinutes = longestGapMinutes,
                        LastComputedAt = now,
                    });
                }
                else
                {
                    existing.PacketsToday = packetsToday;
                    existing.TotalPackets = totalPackets;
                    existing.AveragePacketsPerHour = averagePerHour;
                    existing.LongestGapMinutes = longestGapMinutes;
                    existing.LastComputedAt = now;
                }

                // Recompute HeardVia from last 10 packets for this station.
                // Pull both HopCount and Path so each packet can be classified independently.
                var recentPacketData = await db.Packets
                    .Where(p => p.StationCallsign == callsign)
                    .OrderByDescending(p => p.ReceivedAt)
                    .Take(10)
                    .Select(p => new { p.HopCount, p.Path })
                    .ToListAsync(ct);

                if (recentPacketData.Count > 0)
                {
                    var perPacketVias = recentPacketData
                        .Select(p =>
                        {
                            IReadOnlyList<string> entries = string.IsNullOrEmpty(p.Path)
                                ? []
                                : p.Path.Split(',', StringSplitOptions.RemoveEmptyEntries);
                            return AprsPathParser.ClassifyHeardVia(entries);
                        })
                        .ToList();

                    var hasDirectRf = perPacketVias.Any(v => v == HeardVia.Direct);
                    var hasDigi = perPacketVias.Any(v => v == HeardVia.Digi);

                    station.HeardVia = hasDirectRf && hasDigi
                        ? HeardVia.DirectAndDigi
                        : perPacketVias[0];  // most recent packet's classification
                }

                // Fixed station detection
                // A station is classified Fixed when it has broadcast from the same
                // location for at least FixedDetectionWindowHours without moving by
                // more than MovementThresholdDeg.
                //
                // Rules:
                //   • Only Unknown stations are promoted to Fixed.
                //   • Mobile → Fixed is intentionally blocked; once a station has
                //     been seen moving it remains Mobile indefinitely.
                //   • Fixed → Mobile is handled in HandlePosition via movement delta.
                if (station.StationType == StationType.Unknown)
                {
                    var windowStart = now.AddHours(-FixedDetectionWindowHours);

                    var recentPositions = await db.Packets
                        .Where(p => p.StationCallsign == callsign
                                 && p.ReceivedAt >= windowStart
                                 && p.Latitude != null
                                 && p.Longitude != null)
                        .Select(p => new { p.Latitude, p.Longitude, p.ReceivedAt })
                        .ToListAsync(ct);

                    if (recentPositions.Count >= FixedDetectionMinPackets)
                    {
                        var firstTime = recentPositions.Min(p => p.ReceivedAt);
                        var lastTime = recentPositions.Max(p => p.ReceivedAt);

                        if ((lastTime - firstTime).TotalHours >= FixedDetectionWindowHours)
                        {
                            var refLat = recentPositions[0].Latitude!.Value;
                            var refLon = recentPositions[0].Longitude!.Value;

                            var allWithinThreshold = recentPositions.All(p =>
                                Math.Abs(p.Latitude!.Value - refLat) <= PacketDecoder.MovementThresholdDeg &&
                                Math.Abs(p.Longitude!.Value - refLon) <= PacketDecoder.MovementThresholdDeg);

                            if (allWithinThreshold)
                                station.StationType = StationType.Fixed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to update statistics for {Callsign}.", callsign);
            }
        }

        // One commit for the whole batch's statistics.
        await db.SaveChangesAsync(ct);
    }

    private static InboxMessageDto ToInboxDto(Message m) => new()
    {
        Id = m.Id,
        FromCallsign = m.FromCallsign,
        ToCallsign = m.ToCallsign,
        Body = m.Body,
        MessageId = m.MessageId,
        ReceivedAt = m.ReceivedAt,
        IsRead = m.IsRead,
        AckSent = m.AckSent,
        RetryCount = m.RetryCount,
        MaxRetries = m.MaxRetries,
        NextRetryAt = m.NextRetryAt,
        RetryState = m.RetryState,
        LastSentAt = m.LastSentAt,
    };

    private async Task CheckOwnBeaconAsync(
        DbPacket packet,
        List<Radio> activeRadios,
        DireControlContext db,
        CancellationToken ct)
    {
        var matched = CallsignMatcher.FindMatchingRadio(packet, activeRadios);
        if (matched is null)
            return;

        logger.LogDebug(
            "Own callsign detected: source={Source} hopCount={HopCount} path={Path}",
            packet.StationCallsign, packet.HopCount, packet.Path);

        try
        {
            if (packet.HopCount == 0)
                await RecordOwnBeaconAsync(matched, packet, db, ct);
            else
                await RecordDigiConfirmationAsync(matched, packet, db, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record own-beacon data for radio {Id}.", matched.Id);
        }
    }

    private async Task RecordOwnBeaconAsync(Radio radio, DbPacket packet, DireControlContext db, CancellationToken ct)
    {
        var dedupWindow = packet.ReceivedAt.AddSeconds(-30);

        // Look for a pending (HopCount == -2, Heard == false) beacon recorded by
        // BeaconService within the last 30 s.  If found, upgrade it to confirmed.
        var pending = await db.OwnBeacons
            .FirstOrDefaultAsync(b => b.RadioId == radio.Id && b.HopCount == -2 && !b.Heard && b.BeaconedAt >= dedupWindow, ct);

        if (pending is not null)
        {
            pending.HopCount = 0;
            pending.Heard = true;
            await db.SaveChangesAsync(ct);

            await hubContext.Clients.All.SendAsync(PacketHub.BeaconConfirmedHeardMethod, new BeaconConfirmedHeardDto
            {
                RadioId = radio.Id,
                BeaconId = pending.Id,
            }, ct);

            logger.LogDebug(
                "Confirmed own beacon for {Callsign} (KISS echo received).",
                radio.FullCallsign);
            return;
        }

        // Also suppress if a fully confirmed (HopCount == 0) record already exists
        // within 30 s — prevents duplicates when the echo arrives after a prior echo.
        var alreadyConfirmed = await db.OwnBeacons
            .AnyAsync(b => b.RadioId == radio.Id && b.HopCount == 0 && b.BeaconedAt >= dedupWindow, ct);

        if (alreadyConfirmed)
        {
            logger.LogDebug(
                "Skipping duplicate own-beacon KISS echo for {Callsign} — already confirmed within 30 s.",
                radio.FullCallsign);
            return;
        }

        // No prior record — beacon originated outside DireControl (e.g. Direwolf
        // timer beacon or heard via APRS-IS).  Create a new confirmed record.
        var beacon = new OwnBeacon
        {
            RadioId = radio.Id,
            BeaconedAt = packet.ReceivedAt,
            Latitude = packet.Latitude,
            Longitude = packet.Longitude,
            Comment = string.IsNullOrEmpty(packet.Comment) ? null : packet.Comment,
            PathUsed = string.IsNullOrEmpty(packet.Path) ? null : packet.Path,
            HopCount = 0,
            Heard = true,
        };

        db.OwnBeacons.Add(beacon);
        await db.SaveChangesAsync(ct);

        await hubContext.Clients.All.SendAsync(PacketHub.OwnBeaconReceivedMethod, new OwnBeaconBroadcastDto
        {
            RadioId = radio.Id,
            BeaconId = beacon.Id,
            FullCallsign = radio.FullCallsign,
            BeaconedAt = beacon.BeaconedAt,
            Lat = beacon.Latitude,
            Lon = beacon.Longitude,
            PathUsed = beacon.PathUsed,
            Heard = true,
        }, ct);

        logger.LogDebug("Recorded own beacon for {Callsign} at {Time}.", radio.FullCallsign, beacon.BeaconedAt);
    }

    private async Task RecordDigiConfirmationAsync(Radio radio, DbPacket packet, DireControlContext db, CancellationToken ct)
    {
        var now = packet.ReceivedAt;
        var window = now.AddSeconds(-90);

        // Find the most recent own beacon (HopCount >= -2 includes unconfirmed sends,
        // excludes -1 placeholders) within 90 s.
        var ownBeacon = await db.OwnBeacons
            .Where(b => b.RadioId == radio.Id && b.BeaconedAt >= window && b.HopCount >= -2 && b.HopCount != -1)
            .OrderByDescending(b => b.BeaconedAt)
            .FirstOrDefaultAsync(ct);

        if (ownBeacon is null)
        {
            // Digi echo arrived before (or without) a direct KISS echo — create placeholder.
            ownBeacon = new OwnBeacon
            {
                RadioId = radio.Id,
                BeaconedAt = now,
                Latitude = packet.Latitude,
                Longitude = packet.Longitude,
                Comment = string.IsNullOrEmpty(packet.Comment) ? null : packet.Comment,
                PathUsed = string.IsNullOrEmpty(packet.Path) ? null : packet.Path,
                HopCount = -1,
                Heard = true,
            };

            db.OwnBeacons.Add(ownBeacon);
            await db.SaveChangesAsync(ct);

            // No direct KISS echo was available, so the frontend will never receive an
            // ownBeaconReceived event for this beacon.  Broadcast one now so the "last
            // beacon" panel updates without requiring a page refresh.
            await hubContext.Clients.All.SendAsync(PacketHub.OwnBeaconReceivedMethod, new OwnBeaconBroadcastDto
            {
                RadioId = radio.Id,
                BeaconId = ownBeacon.Id,
                FullCallsign = radio.FullCallsign,
                BeaconedAt = ownBeacon.BeaconedAt,
                Lat = ownBeacon.Latitude,
                Lon = ownBeacon.Longitude,
                PathUsed = ownBeacon.PathUsed,
                Heard = true,
            }, ct);
        }

        // A digi relayed our beacon — that confirms it was heard on RF.
        if (!ownBeacon.Heard)
        {
            ownBeacon.Heard = true;
            await hubContext.Clients.All.SendAsync(PacketHub.BeaconConfirmedHeardMethod, new BeaconConfirmedHeardDto
            {
                RadioId = radio.Id,
                BeaconId = ownBeacon.Id,
            }, ct);
        }

        // Identify relaying digipeater from resolved path.
        // ResolvedPath layout after full processing: [0]=source, [1..n-1]=digi hops, [n]=home.
        var intermediateHops = packet.ResolvedPath
            .Where(e => e.HopIndex > 0 && e.HopIndex < packet.ResolvedPath.Count - 1)
            .OrderBy(e => e.HopIndex)
            .ToList();

        var digiEntry = intermediateHops.FirstOrDefault(e => !AprsPathParser.IsGenericAlias(e.Callsign))
                     ?? intermediateHops.FirstOrDefault();

        var digiCallsign = digiEntry?.Callsign ?? "UNKNOWN";
        var digiLat = digiEntry?.Latitude;
        var digiLon = digiEntry?.Longitude;
        var aliasUsed = digiEntry?.AliasUsed;

        // Fall back to Stations table if coordinates are not in the path.
        if (digiLat is null && digiCallsign != "UNKNOWN" && !AprsPathParser.IsGenericAlias(digiCallsign))
        {
            var digiStation = await db.Stations.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Callsign == digiCallsign, ct);
            digiLat = digiStation?.LastLat;
            digiLon = digiStation?.LastLon;
        }

        var secondsAfter = Math.Max(0, (int)(now - ownBeacon.BeaconedAt).TotalSeconds);

        var existingConfirmation = await db.DigiConfirmations
            .FirstOrDefaultAsync(c =>
                c.OwnBeaconId == ownBeacon.Id &&
                c.DigipeaterCallsign == digiCallsign, ct);

        if (existingConfirmation != null)
        {
            logger.LogDebug(
                "Duplicate confirmation from {Digi} for beacon {Id} — ignoring.",
                digiCallsign, ownBeacon.Id);
            return;
        }

        var confirmation = new DigiConfirmation
        {
            OwnBeaconId = ownBeacon.Id,
            ConfirmedAt = now,
            DigipeaterCallsign = digiCallsign,
            DigipeaterLat = digiLat,
            DigipeaterLon = digiLon,
            AliasUsed = aliasUsed,
            SecondsAfterBeacon = secondsAfter,
        };

        db.DigiConfirmations.Add(confirmation);
        await db.SaveChangesAsync(ct);

        await hubContext.Clients.All.SendAsync(PacketHub.DigiConfirmationMethod, new DigiConfirmationBroadcastDto
        {
            RadioId = radio.Id,
            BeaconId = ownBeacon.Id,
            FullCallsign = radio.FullCallsign,
            Digipeater = digiCallsign,
            ConfirmedAt = now,
            SecondsAfterBeacon = secondsAfter,
            Lat = digiLat,
            Lon = digiLon,
        }, ct);

        logger.LogDebug("Recorded digi confirmation for {Callsign} via {Digi} (+{Secs}s).",
            radio.FullCallsign, digiCallsign, secondsAfter);
    }

}
