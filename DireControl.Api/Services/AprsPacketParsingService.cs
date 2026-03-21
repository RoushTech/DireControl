using System.Text.RegularExpressions;
using AprsSharp.AprsParser;
using DireControl.Api.Controllers.Models;
using DireControl.Api.Hubs;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using DireControl.PathParsing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AprsPacketType = AprsSharp.AprsParser.PacketType;
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
    IOptions<DireControlOptions> options,
    MessageSendingService messageSendingService,
    PendingAlertChannel alertChannel,
    ILogger<AprsPacketParsingService> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private const int PollIntervalMs = 5_000;

    /// <summary>
    /// Minimum position delta (decimal degrees) treated as meaningful movement.
    /// ~111 m at the equator; generous enough to absorb GPS jitter.
    /// </summary>
    private const double MovementThresholdDeg = 0.001;

    /// <summary>
    /// A station must have position packets spanning at least this many hours,
    /// all within <see cref="MovementThresholdDeg"/>, before it is classified Fixed.
    /// </summary>
    private const int FixedDetectionWindowHours = 4;

    /// <summary>
    /// Minimum number of position packets required within the Fixed detection window
    /// before the classification fires (avoids single-packet false positives).
    /// </summary>
    private const int FixedDetectionMinPackets = 3;

    /// <summary>
    /// Two-character APRS symbol strings (table + code) that unambiguously represent
    /// a mobile platform.  A match immediately classifies the transmitting station
    /// as <see cref="StationType.Mobile"/> unless a higher-priority type is already set.
    /// </summary>
    private static readonly HashSet<string> MobileSymbols = new(StringComparer.Ordinal)
    {
        // ── Primary table (/) ────────────────────────────────────────────────
        "/'",   // Small aircraft
        "/<",   // Motorcycle
        "/>",   // Car
        "/[",   // Jogger / runner
        "/^",   // Large aircraft
        "/b",   // Bicycle
        "/g",   // Glider
        "/j",   // Jeep
        "/k",   // Truck
        "/s",   // Power boat
        "/u",   // Bus
        "/v",   // Van / SUV
        "/X",   // Helicopter
        "/Y",   // Sailboat (yacht)
        // ── Alternate table (\) ──────────────────────────────────────────────
        "\\>",  // Car
        "\\j",  // Jeep
        "\\k",  // Truck
        "\\u",  // Bus
    };

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

        // Pre-load all stations for this batch so UpdateStation can find them in
        // db.Stations.Local without issuing a separate query per packet.
        var callsigns = packets.Select(p => p.StationCallsign).Distinct().ToList();
        await db.Stations.Where(s => callsigns.Contains(s.Callsign)).LoadAsync(ct);

        var ourCallsign = options.Value.OurCallsign.Trim();

        // Pre-load active radios so own-beacon detection doesn't need per-packet DB queries.
        var activeRadios = await db.Radios.Where(r => r.IsActive).ToListAsync(ct);

        foreach (var packet in packets)
        {
            var effects = new List<MessageEffect>();
            try
            {
                await ParsePacketAsync(packet, db, ourCallsign, effects, ct);
                await ResolvePathCoordinatesAsync(packet, db, ourCallsign, ct);
                await db.SaveChangesAsync(ct);

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
                    Summary = BuildSummary(packet),
                    HopCount = packet.HopCount,
                    ResolvedPath = packet.ResolvedPath,
                };

                await hubContext.Clients.All.SendAsync(PacketHub.PacketReceivedMethod, update, ct);

                // Notify alerting service of updated station
                alertChannel.Writer.TryWrite(packet.StationCallsign);
            }
            catch (Exception ex)
            {
                logger.LogTrace(ex, "Could not parse packet {Id} ({Raw}).", packet.Id, packet.RawPacket);
                // Discard any partial changes and mark this packet so it is not re-queued.
                db.ChangeTracker.Clear();
                await db.Packets
                    .Where(p => p.Id == packet.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.ParsedType, OurPacketType.Unparseable), ct);
            }
        }

        await UpdateStationStatisticsAsync(db, callsigns, ct);
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
                // Send auto-ACK and mark AckSent = true
                messageSendingService.SendAck(effect.PeerCallsign, effect.MessageId);

                var msg = db.Messages.Local.FirstOrDefault(m =>
                    m.FromCallsign == effect.PeerCallsign &&
                    m.MessageId == effect.MessageId &&
                    m.ToCallsign.Equals(ourCallsign, StringComparison.OrdinalIgnoreCase));

                if (msg is not null)
                {
                    msg.AckSent = true;
                    await db.SaveChangesAsync(ct);
                }

                // Broadcast to frontend
                if (msg is not null)
                {
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
                        PacketHub.MessageAcknowledgedMethod,
                        new MessageAcknowledgedDto { Id = dbId, MessageId = effect.OriginalMsgId },
                        ct);

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
                messageSendingService.SendAck(effect.PeerCallsign, effect.MessageId);
            }
        }
    }

    private async Task ParsePacketAsync(DbPacket packet, DireControlContext db, string ourCallsign, List<MessageEffect> effects, CancellationToken ct)
    {
        var aprs = new AprsSharp.AprsParser.Packet(packet.RawPacket);

        packet.ParsedType = MapPacketType(aprs.InfoField?.Type ?? AprsPacketType.Unknown);

        // AprsSharp 0.4.1 reports a Position-family type enum for @ prefix weather
        // packets even though InfoField IS a WeatherInfo instance.  Override here so
        // ParsedType always matches the InfoField class when weather data is present.
        if (aprs.InfoField is WeatherInfo)
            packet.ParsedType = OurPacketType.Weather;

        // Extract path from the raw TNC2 string.  ParseTnc2Header reads directly
        // from RawPacket so asterisk markers from the AX.25 H-bit are preserved;
        // the returned RawPath already excludes the TOCALL.
        var (_, tocall, rawPath) = AprsPathParser.ParseTnc2Header(packet.RawPacket);

        packet.Path = rawPath;   // e.g. "WE4MB-3*,WIDE2" — TOCALL absent, asterisks intact

        // ExtractViaHops expects the full path list with TOCALL at index [0] so it
        // can unconditionally skip it.  Reconstruct that list here.
        List<string> fullPathList = string.IsNullOrEmpty(tocall)
            ? []
            : string.IsNullOrEmpty(rawPath)
                ? [tocall]
                : rawPath
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Prepend(tocall)
                    .ToList();

        // Full ResolvedPath (with source + home + coordinates) is built in ResolvePathCoordinatesAsync.
        var (viaHops, hopCount) = AprsPathParser.ExtractViaHops(fullPathList);
        packet.HopCount = hopCount;
        packet.ResolvedPath = viaHops;

        switch (aprs.InfoField)
        {
            case WeatherInfo weather:
                HandleWeather(packet, weather, db);
                break;

            case PositionInfo position:
                HandlePosition(packet, position, db);
                break;

            case MessageInfo message:
                await HandleMessageAsync(packet, message, db, ourCallsign, effects, ct);
                break;

            case StatusInfo status:
                packet.Comment = status.Comment ?? string.Empty;
                break;
        }

        // _ prefix weather packets: AprsSharp sets the type enum to WeatherReport but
        // InfoField is UnsupportedInfo (library limitation), so HandleWeather never fires
        // and the station is never marked.  Correct that here.
        if (packet.ParsedType == OurPacketType.Weather && packet.WeatherData == null)
        {
            UpdateStation(db, packet.StationCallsign, s =>
            {
                s.IsWeatherStation = true;
                s.StationType = StationType.Weather;
            });
        }

        // ── Mode / frequency / gateway detection ────────────────────────────────
        // Extract operating mode from TOCALL prefix and frequency from the comment.
        // Gateway station type is set when the TOCALL indicates a digital voice
        // gateway (D-Star, DMR, etc.) — this takes priority over Unknown/Fixed but
        // never overwrites Weather, Digipeater, or IGate.
        var mode = DetectMode(tocall, packet.Comment);
        var freq = ParseFrequency(packet.Comment);

        if (mode != null || freq != null)
        {
            UpdateStation(db, packet.StationCallsign, s =>
            {
                if (mode != null) s.LastMode = mode;
                if (freq != null) s.LastFrequencyMhz = freq;

                if (IsGatewayTocall(tocall) &&
                    s.StationType is StationType.Unknown or StationType.Fixed)
                {
                    s.StationType = StationType.Gateway;
                }
            });
        }

        // Third-party packets (info field starts with '}'): the outer source is the
        // igate that forwarded the packet; the actual sender and payload live in the
        // inner TNC2 string.  AprsSharp 0.4.1 provides no structured
        // ThirdPartyTrafficInfo class, so we extract the inner string manually and
        // re-parse it.  Only message packets inside third-party frames are handled;
        // other inner types are left as Unparseable.
        if (aprs.InfoField?.Type == AprsPacketType.ThirdPartyTraffic)
        {
            if (MessageHandlingLogic.TryExtractThirdPartyInner(
                    packet.RawPacket, out var innerRaw, out var innerSender))
            {
                try
                {
                    var innerAprs = new AprsSharp.AprsParser.Packet(innerRaw);
                    if (innerAprs.InfoField is MessageInfo innerMsg)
                    {
                        packet.ParsedType = OurPacketType.Message;
                        await HandleMessageAsync(packet, innerMsg, db, ourCallsign, effects, ct,
                            senderCallsignOverride: innerSender);
                    }
                }
                catch
                {
                    // inner packet unparseable — leave ParsedType as Unparseable
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Station statistics
    // -------------------------------------------------------------------------

    private async Task UpdateStationStatisticsAsync(
        DireControlContext db,
        IEnumerable<string> callsigns,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var todayStart = DateTime.Now.Date.ToUniversalTime();

        foreach (var callsign in callsigns)
        {
            try
            {
                var station = db.Stations.Local.FirstOrDefault(s => s.Callsign == callsign);
                if (station is null)
                    continue;

                // Use lightweight COUNT queries instead of loading all timestamps.
                var totalPackets = await db.Packets
                    .CountAsync(p => p.StationCallsign == callsign, ct);

                if (totalPackets == 0)
                    continue;

                var packetsToday = await db.Packets
                    .CountAsync(p => p.StationCallsign == callsign && p.ReceivedAt >= todayStart, ct);

                var ageHours = Math.Max(1.0, (now - station.FirstSeen).TotalHours);
                var averagePerHour = totalPackets / ageHours;

                // Incrementally maintain longest gap: compare the gap between the two
                // most recent packets against the stored maximum.
                var existing = await db.StationStatistics
                    .FirstOrDefaultAsync(ss => ss.Callsign == callsign, ct);

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
                        AveragePacketsPerHour = averagePerHour,
                        LongestGapMinutes = longestGapMinutes,
                        LastComputedAt = now,
                    });
                }
                else
                {
                    existing.PacketsToday = packetsToday;
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
                            return AprsPathParser.ClassifyHeardVia(entries, p.HopCount);
                        })
                        .ToList();

                    var hasDirectRf = perPacketVias.Any(v => v == HeardVia.Direct);
                    var hasDigi     = perPacketVias.Any(v => v == HeardVia.Digi);

                    station.HeardVia = hasDirectRf && hasDigi
                        ? HeardVia.DirectAndDigi
                        : perPacketVias[0];  // most recent packet's classification
                }

                // ── Fixed station detection ──────────────────────────────────────────
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
                        var lastTime  = recentPositions.Max(p => p.ReceivedAt);

                        if ((lastTime - firstTime).TotalHours >= FixedDetectionWindowHours)
                        {
                            var refLat = recentPositions[0].Latitude!.Value;
                            var refLon = recentPositions[0].Longitude!.Value;

                            var allWithinThreshold = recentPositions.All(p =>
                                Math.Abs(p.Latitude!.Value  - refLat) <= MovementThresholdDeg &&
                                Math.Abs(p.Longitude!.Value - refLon) <= MovementThresholdDeg);

                            if (allWithinThreshold)
                                station.StationType = StationType.Fixed;
                        }
                    }
                }

                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to update statistics for {Callsign}.", callsign);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Per-type handlers
    // -------------------------------------------------------------------------

    private void HandlePosition(DbPacket packet, PositionInfo info, DireControlContext db)
    {
        packet.Comment = info.Comment ?? string.Empty;

        if (info.Position is { } pos)
        {
            var coord = pos.Coordinates;
            if (!double.IsNaN(coord.Latitude) && !double.IsNaN(coord.Longitude))
            {
                packet.Latitude = coord.Latitude;
                packet.Longitude = coord.Longitude;
            }

            var symbol = $"{pos.SymbolTableIdentifier}{pos.SymbolCode}";

            UpdateStation(db, packet.StationCallsign, station =>
            {
                // ── Mobile detection ────────────────────────────────────────
                // Check BEFORE updating LastLat/LastLon so we compare old vs new.
                //
                // Two independent signals both upgrade a station to Mobile:
                //   1. Symbol — the station is transmitting a known vehicle icon.
                //   2. Movement — the position differs from the last known position
                //      by more than the GPS-jitter threshold (~111 m).
                //
                // Only Unknown and Fixed stations may be promoted to Mobile.
                // Fixed → Mobile is intentionally allowed (station started moving).
                // Mobile → Fixed is NOT allowed (handled by time-based detection).
                // Weather / Digipeater / IGate are never overwritten here.
                var isMobileSymbol = MobileSymbols.Contains(symbol);
                var hasMoved = station.LastLat is not null
                    && station.LastLon is not null
                    && !double.IsNaN(coord.Latitude)
                    && !double.IsNaN(coord.Longitude)
                    && (Math.Abs(coord.Latitude  - station.LastLat.Value) > MovementThresholdDeg
                     || Math.Abs(coord.Longitude - station.LastLon.Value) > MovementThresholdDeg);

                if ((isMobileSymbol || hasMoved) &&
                    station.StationType is StationType.Unknown or StationType.Fixed)
                {
                    station.StationType = StationType.Mobile;
                }

                if (!double.IsNaN(coord.Latitude)) station.LastLat = coord.Latitude;
                if (!double.IsNaN(coord.Longitude)) station.LastLon = coord.Longitude;
                station.Symbol = symbol;
            });
        }
    }

    private void HandleWeather(DbPacket packet, WeatherInfo info, DireControlContext db)
    {
        HandlePosition(packet, info, db);

        // Rainfall fields in AprsSharp are in 100ths of an inch; convert to inches.
        // Pressure in AprsSharp is in tenths of mb; convert to mb.
        logger.LogDebug("APRSSharp pressure={Pressure} for packet {Raw}", info.BarometricPressure, packet.RawPacket);
        packet.WeatherData = new WeatherData
        {
            TemperatureF = (double?)info.Temperature,
            WindSpeedMph = (double?)info.WindSpeed,
            WindDirectionDeg = info.WindDirection,
            WindGustMph = (double?)info.WindGust,
            HumidityPercent = info.Humidity,
            PressureMbar = (double?)info.BarometricPressure / 10.0,
            RainfallLastHourIn = info.Rainfall1Hour.HasValue ? info.Rainfall1Hour.Value / 100.0 : null,
            RainfallLast24hIn = info.Rainfall24Hour.HasValue ? info.Rainfall24Hour.Value / 100.0 : null,
            RainfallSinceMidnightIn = info.RainfallSinceMidnight.HasValue ? info.RainfallSinceMidnight.Value / 100.0 : null,
        };

        UpdateStation(db, packet.StationCallsign, s =>
        {
            s.IsWeatherStation = true;
            s.StationType = StationType.Weather;
        });
    }

    private async Task HandleMessageAsync(
        DbPacket packet,
        MessageInfo info,
        DireControlContext db,
        string ourCallsign,
        List<MessageEffect> effects,
        CancellationToken ct,
        string? senderCallsignOverride = null)
    {
        var fromCallsign = senderCallsignOverride ?? packet.StationCallsign;
        var addressee = info.Addressee ?? string.Empty;
        var body = info.Content ?? string.Empty;
        var messageId = info.Id ?? string.Empty;

        packet.MessageData = new MessageData
        {
            Addressee = addressee,
            Text = body,
            MessageId = messageId,
        };

        if (string.IsNullOrWhiteSpace(ourCallsign))
            return;

        if (!addressee.Trim().Equals(ourCallsign, StringComparison.OrdinalIgnoreCase))
            return;

        // Detect ACK receipts: body is "ackXXXX" where XXXX is the original message ID.
        if (MessageHandlingLogic.TryParseAck(body, out var originalMsgId))
        {
            effects.Add(new MessageEffect(
                IsNewInboxMessage: false,
                IsAckReceived: true,
                PeerCallsign: fromCallsign,
                MessageId: messageId,
                OriginalMsgId: originalMsgId));
            return;
        }

        // Dedup: if we already have a message from this sender with this ID, the
        // remote station is retransmitting because our ACK never reached it.
        // Re-queue an ACK but skip adding a duplicate inbox entry.
        if (!string.IsNullOrWhiteSpace(messageId))
        {
            var isDuplicate = await MessageHandlingLogic.IsMessageDuplicateAsync(
                fromCallsign, messageId, db, ct);

            if (isDuplicate)
            {
                effects.Add(new MessageEffect(
                    IsNewInboxMessage: false,
                    IsAckReceived: false,
                    PeerCallsign: fromCallsign,
                    MessageId: messageId,
                    IsDuplicateInboxMessage: true));
                return;
            }
        }

        // Regular message addressed to us — add to inbox.
        db.Messages.Add(new Message
        {
            FromCallsign = fromCallsign,
            ToCallsign = addressee.Trim(),
            Body = body,
            MessageId = messageId,
            ReceivedAt = packet.ReceivedAt,
            IsRead = false,
            AckSent = false,
            ReplySent = false,
        });

        if (!string.IsNullOrWhiteSpace(messageId))
        {
            effects.Add(new MessageEffect(
                IsNewInboxMessage: true,
                IsAckReceived: false,
                PeerCallsign: fromCallsign,
                MessageId: messageId));
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the complete <see cref="DbPacket.ResolvedPath"/> for a packet:
    /// <list type="bullet">
    ///   <item>HopIndex 0 — originating station (position from packet or Station table)</item>
    ///   <item>HopIndex 1…n-1 — intermediate digipeater hops (already populated by ParsePacket)</item>
    ///   <item>HopIndex n — our own station (home position from settings)</item>
    /// </list>
    /// Coordinates are resolved from <see cref="DireControlContext.Stations"/> and
    /// <see cref="DireControlOptions.HomeLat"/> / <see cref="DireControlOptions.HomeLon"/>.
    /// </summary>
    private async Task ResolvePathCoordinatesAsync(
        DbPacket packet,
        DireControlContext db,
        string ourCallsign,
        CancellationToken ct)
    {
        var opts = options.Value;

        // --- Hop 0: originating station ---
        double? srcLat = packet.Latitude;
        double? srcLon = packet.Longitude;
        if (srcLat == null || srcLon == null)
        {
            var srcStation = db.Stations.Local.FirstOrDefault(s => s.Callsign == packet.StationCallsign)
                ?? await db.Stations.FindAsync(new object?[] { packet.StationCallsign }, ct);
            srcLat = srcStation?.LastLat;
            srcLon = srcStation?.LastLon;
        }

        var sourceEntry = new ResolvedPathEntry
        {
            Callsign = packet.StationCallsign,
            Latitude = srcLat,
            Longitude = srcLon,
            Known = srcLat != null && srcLon != null,
            HopIndex = 0,
        };

        // --- Intermediate hops (already extracted by ParsePacket, HopIndex 1+) ---
        foreach (var hop in packet.ResolvedPath)
        {
            if (AprsPathParser.IsGenericAlias(hop.Callsign))
            {
                hop.Known = false;
                continue;
            }

            var station = db.Stations.Local.FirstOrDefault(s => s.Callsign == hop.Callsign)
                ?? await db.Stations.FindAsync(new object?[] { hop.Callsign }, ct);

            // Station appears starred in this packet's path — it acted as a digipeater.
            // Only set when Unknown; never downgrade a more specific type (e.g. IGate).
            if (station != null && station.StationType == StationType.Unknown)
                station.StationType = StationType.Digipeater;

            if (station?.LastLat != null && station.LastLon != null)
            {
                hop.Latitude = station.LastLat;
                hop.Longitude = station.LastLon;
                hop.Known = true;
            }
        }

        packet.UnknownHopCount = packet.ResolvedPath.Count(e => !e.Known);

        // Detect igate: the callsign immediately after qAR or qAS in the path
        // is the station that gated this packet from RF to APRS-IS.
        // IGate takes priority over Digipeater (promote if already marked Digipeater).
        var pathParts = (packet.Path ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < pathParts.Length - 1; i++)
        {
            var token = pathParts[i].TrimEnd('*');
            if (token.Equals("qAR", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("qAS", StringComparison.OrdinalIgnoreCase))
            {
                var igateCs = pathParts[i + 1];
                if (!AprsPathParser.IsGenericAlias(igateCs))
                {
                    var igateStn = db.Stations.Local.FirstOrDefault(s => s.Callsign == igateCs)
                        ?? await db.Stations.FindAsync(new object?[] { igateCs }, ct);
                    if (igateStn != null &&
                        igateStn.StationType is StationType.Unknown or StationType.Digipeater)
                    {
                        igateStn.StationType = StationType.IGate;
                    }
                }
                break;
            }
        }

        // --- Final hop: our own station ---
        var homeEntry = new ResolvedPathEntry
        {
            Callsign = ourCallsign,
            Latitude = opts.HomeLat,
            Longitude = opts.HomeLon,
            Known = opts.HomeLat != null && opts.HomeLon != null,
            HopIndex = packet.ResolvedPath.Count + 1,
        };

        // Prepend source and append home so the list is fully ordered
        packet.ResolvedPath.Insert(0, sourceEntry);
        packet.ResolvedPath.Add(homeEntry);
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
        ReplySent = m.ReplySent,
        RetryCount = m.RetryCount,
        MaxRetries = m.MaxRetries,
        NextRetryAt = m.NextRetryAt,
        RetryState = m.RetryState,
        LastSentAt = m.LastSentAt,
    };

    private static void UpdateStation(DireControlContext db, string callsign, Action<Station> update)
    {
        var station = db.Stations.Local.FirstOrDefault(s => s.Callsign == callsign);
        if (station is not null)
            update(station);
    }

    private static OurPacketType MapPacketType(AprsPacketType type) => type switch
    {
        AprsPacketType.PositionWithoutTimestampNoMessaging or
        AprsPacketType.PositionWithTimestampNoMessaging or
        AprsPacketType.PositionWithoutTimestampWithMessaging or
        AprsPacketType.PositionWithTimestampWithMessaging or
        AprsPacketType.CurrentMicEData or
        AprsPacketType.OldMicEData or
        AprsPacketType.OldMicEDataCurrentTMD700 or
        AprsPacketType.CurrentMicEDataNotTMD700 or
        AprsPacketType.MaidenheadGridLocatorBeacon => OurPacketType.Position,

        AprsPacketType.Message => OurPacketType.Message,

        AprsPacketType.WeatherReport or
        AprsPacketType.PeetBrosUIIWeatherStation => OurPacketType.Weather,

        AprsPacketType.TelemetryData => OurPacketType.Telemetry,

        AprsPacketType.Object => OurPacketType.Object,

        AprsPacketType.Item => OurPacketType.Item,

        AprsPacketType.Status => OurPacketType.Status,

        _ => OurPacketType.Unparseable,
    };

    private static string BuildSummary(DbPacket packet)
    {
        return packet.ParsedType switch
        {
            OurPacketType.Position => packet.Latitude is not null && packet.Longitude is not null
                ? $"Position at {packet.Latitude:F5}, {packet.Longitude:F5}"
                : "Position packet",

            OurPacketType.Message => packet.MessageData is not null
                ? $"Message to {packet.MessageData.Addressee}: {packet.MessageData.Text}"
                : "Message packet",

            OurPacketType.Weather => packet.WeatherData?.TemperatureF is { } t
                ? $"Weather report, temperature {t:F1}°F"
                : "Weather packet",

            OurPacketType.Telemetry => "Telemetry packet",
            OurPacketType.Object => "Object packet",
            OurPacketType.Item => "Item packet",

            OurPacketType.Status => string.IsNullOrWhiteSpace(packet.Comment)
                ? "Status packet"
                : $"Status: {packet.Comment}",

            _ => string.IsNullOrWhiteSpace(packet.RawPacket)
                ? "Unrecognized packet"
                : $"Raw: {packet.RawPacket}"
        };
    }

    // ─── Own-beacon detection ─────────────────────────────────────────────────

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
        // Deduplication: BeaconNowAsync records a HopCount=0 entry at send time.  When
        // Direwolf's KISS echo arrives shortly afterwards it would create a duplicate.
        // Suppress the echo if a matching entry already exists within 30 seconds.
        var dedupWindow = packet.ReceivedAt.AddSeconds(-30);
        var alreadyRecorded = await db.OwnBeacons
            .AnyAsync(b => b.RadioId == radio.Id && b.HopCount == 0 && b.BeaconedAt >= dedupWindow, ct);

        if (alreadyRecorded)
        {
            logger.LogDebug(
                "Skipping duplicate own-beacon KISS echo for {Callsign} — already recorded within 30 s.",
                radio.FullCallsign);
            return;
        }

        var beacon = new OwnBeacon
        {
            RadioId = radio.Id,
            BeaconedAt = packet.ReceivedAt,
            Latitude = packet.Latitude,
            Longitude = packet.Longitude,
            Comment = string.IsNullOrEmpty(packet.Comment) ? null : packet.Comment,
            PathUsed = string.IsNullOrEmpty(packet.Path) ? null : packet.Path,
            HopCount = 0,
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
        }, ct);

        logger.LogDebug("Recorded own beacon for {Callsign} at {Time}.", radio.FullCallsign, beacon.BeaconedAt);
    }

    private async Task RecordDigiConfirmationAsync(Radio radio, DbPacket packet, DireControlContext db, CancellationToken ct)
    {
        var now = packet.ReceivedAt;
        var window = now.AddSeconds(-90);

        // Find the most recent direct echo (HopCount >= 0 excludes placeholders) within 90 s.
        var ownBeacon = await db.OwnBeacons
            .Where(b => b.RadioId == radio.Id && b.BeaconedAt >= window && b.HopCount >= 0)
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

    // ─── Mode / frequency helpers ─────────────────────────────────────────────

    private static readonly Regex FrequencyRegex = new(
        @"(\d{2,3}\.\d{3,5})\s*MHz",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Known TOCALL prefixes for digital voice gateway software.
    /// Each entry is matched as a prefix of the TOCALL field.
    /// </summary>
    private static readonly (string Prefix, string Mode)[] GatewayTocallPrefixes =
    [
        ("APDG",  "D-Star"),   // D-Star gateways (ircDDB Gateway, …)
        ("APDS",  "D-Star"),   // D-Star (dstar.is)
        ("APDP",  "D-Star"),   // D-PRS (D-Star position reporting)
        ("APDMR", "DMR"),      // DMR gateways
        ("APBM",  "DMR"),      // BrandMeister DMR
        ("APRX",  "DMR"),      // DMR repeaters (various)
        ("APYSF", "YSF"),      // Yaesu System Fusion
        ("APWIR", "WIRES-X"),  // Yaesu WIRES-X
    ];

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="tocall"/> matches
    /// a known digital voice gateway TOCALL prefix.
    /// </summary>
    internal static bool IsGatewayTocall(string? tocall)
    {
        if (string.IsNullOrEmpty(tocall)) return false;
        foreach (var (prefix, _) in GatewayTocallPrefixes)
        {
            if (tocall.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Detects the operating mode from the TOCALL prefix and/or comment text.
    /// Returns null when mode cannot be determined.
    /// </summary>
    internal static string? DetectMode(string? tocall, string? comment)
    {
        // 1. Check TOCALL prefix first — most reliable signal.
        if (!string.IsNullOrEmpty(tocall))
        {
            foreach (var (prefix, mode) in GatewayTocallPrefixes)
            {
                if (tocall.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return mode;
            }
        }

        // 2. Fall back to comment text keywords.
        if (!string.IsNullOrEmpty(comment))
        {
            if (comment.Contains("D-Star", StringComparison.OrdinalIgnoreCase) ||
                comment.Contains("DStar", StringComparison.OrdinalIgnoreCase))
                return "D-Star";
            if (comment.Contains("DMR", StringComparison.OrdinalIgnoreCase))
                return "DMR";
            if (comment.Contains("YSF", StringComparison.OrdinalIgnoreCase) ||
                comment.Contains("System Fusion", StringComparison.OrdinalIgnoreCase))
                return "YSF";
            if (comment.Contains("WIRES", StringComparison.OrdinalIgnoreCase))
                return "WIRES-X";
            if (comment.Contains("AllStar", StringComparison.OrdinalIgnoreCase) ||
                comment.Contains("EchoLink", StringComparison.OrdinalIgnoreCase))
                return "AllStar";
        }

        return null;
    }

    /// <summary>
    /// Extracts the first frequency (in MHz) from a packet comment.
    /// Returns the numeric string (e.g. "144.96000") or null.
    /// </summary>
    internal static string? ParseFrequency(string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return null;
        var match = FrequencyRegex.Match(comment);
        return match.Success ? match.Groups[1].Value : null;
    }
}

/// <summary>
/// Describes a messaging side-effect that needs to be processed after
/// the packet has been parsed and saved.
/// </summary>
internal sealed record MessageEffect(
    bool IsNewInboxMessage,
    bool IsAckReceived,
    string PeerCallsign,
    string MessageId,
    string? OriginalMsgId = null,
    bool IsDuplicateInboxMessage = false);
