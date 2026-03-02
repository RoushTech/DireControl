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

        foreach (var packet in packets)
        {
            var effects = new List<MessageEffect>();
            try
            {
                ParsePacket(packet, db, ourCallsign, effects);
                await ResolvePathCoordinatesAsync(packet, db, ourCallsign, ct);
                await db.SaveChangesAsync(ct);

                await ApplyMessageEffectsAsync(effects, db, ourCallsign, ct);

                var update = new PacketBroadcastDto
                {
                    Id = packet.Id,
                    Callsign = packet.StationCallsign,
                    ParsedType = packet.ParsedType.ToString(),
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
                // Mark our sent message as ACK'd
                var sentMsg = await db.Messages
                    .FirstOrDefaultAsync(
                        m => m.MessageId == effect.OriginalMsgId &&
                             m.FromCallsign.Equals(ourCallsign, StringComparison.OrdinalIgnoreCase) &&
                             m.ToCallsign.Equals(effect.PeerCallsign, StringComparison.OrdinalIgnoreCase),
                        ct);

                if (sentMsg is not null && !sentMsg.AckSent)
                {
                    sentMsg.AckSent = true;
                    await db.SaveChangesAsync(ct);

                    await hubContext.Clients.All.SendAsync(
                        PacketHub.MessageAckedMethod,
                        new MessageAckDto { Id = sentMsg.Id, MessageId = effect.OriginalMsgId },
                        ct);
                }
            }
        }
    }

    private void ParsePacket(DbPacket packet, DireControlContext db, string ourCallsign, List<MessageEffect> effects)
    {
        var aprs = new AprsSharp.AprsParser.Packet(packet.RawPacket);

        packet.ParsedType = MapPacketType(aprs.InfoField?.Type ?? AprsPacketType.Unknown);
        packet.Path = aprs.Path is { Count: > 0 }
            ? string.Join(",", aprs.Path.OfType<string>())
            : string.Empty;

        // Full ResolvedPath (with source + home + coordinates) is built in ResolvePathCoordinatesAsync.
        var (viaHops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);
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
                HandleMessage(packet, message, db, ourCallsign, effects);
                break;

            case StatusInfo status:
                packet.Comment = status.Comment ?? string.Empty;
                break;
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
        var todayStart = now.Date;

        foreach (var callsign in callsigns)
        {
            try
            {
                var station = db.Stations.Local.FirstOrDefault(s => s.Callsign == callsign);
                if (station is null)
                    continue;

                // Load all packet timestamps for this station (only ReceivedAt needed)
                var allTimes = await db.Packets
                    .Where(p => p.StationCallsign == callsign)
                    .OrderBy(p => p.ReceivedAt)
                    .Select(p => p.ReceivedAt)
                    .ToListAsync(ct);

                if (allTimes.Count == 0)
                    continue;

                var packetsToday = allTimes.Count(t => t >= todayStart);

                // Average packets per hour based on lifetime age vs total count
                var ageHours = Math.Max(1.0, (now - station.FirstSeen).TotalHours);
                var averagePerHour = allTimes.Count / ageHours;

                // Longest gap between consecutive packets
                var longestGapMinutes = 0;
                for (var i = 1; i < allTimes.Count; i++)
                {
                    var gapMin = (int)(allTimes[i] - allTimes[i - 1]).TotalMinutes;
                    if (gapMin > longestGapMinutes)
                        longestGapMinutes = gapMin;
                }

                var existing = await db.StationStatistics
                    .FirstOrDefaultAsync(ss => ss.Callsign == callsign, ct);

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

                // Recompute HeardVia from last 10 packets for this station
                var recentHopCounts = await db.Packets
                    .Where(p => p.StationCallsign == callsign)
                    .OrderByDescending(p => p.ReceivedAt)
                    .Take(10)
                    .Select(p => p.HopCount)
                    .ToListAsync(ct);

                if (recentHopCounts.Count > 0)
                {
                    station.HeardVia = recentHopCounts.All(h => h == 0) ? HeardVia.Direct
                        : recentHopCounts.All(h => h > 0) ? HeardVia.Digi
                        : HeardVia.DirectAndDigi;
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
        packet.WeatherData = new WeatherData
        {
            TemperatureF = (double?)info.Temperature,
            WindSpeedMph = (double?)info.WindSpeed,
            WindDirectionDeg = info.WindDirection,
            WindGustMph = (double?)info.WindGust,
            HumidityPercent = info.Humidity,
            PressureMbar = (double?)info.BarometricPressure,
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

    private void HandleMessage(
        DbPacket packet,
        MessageInfo info,
        DireControlContext db,
        string ourCallsign,
        List<MessageEffect> effects)
    {
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
        if (body.StartsWith("ack", StringComparison.OrdinalIgnoreCase) && body.Length > 3)
        {
            var originalMsgId = body[3..].Trim();
            effects.Add(new MessageEffect(
                IsNewInboxMessage: false,
                IsAckReceived: true,
                PeerCallsign: packet.StationCallsign,
                MessageId: messageId,
                OriginalMsgId: originalMsgId));
            return;
        }

        // Regular message addressed to us — add to inbox.
        db.Messages.Add(new Message
        {
            FromCallsign = packet.StationCallsign,
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
                PeerCallsign: packet.StationCallsign,
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

            if (station?.LastLat != null && station.LastLon != null)
            {
                hop.Latitude = station.LastLat;
                hop.Longitude = station.LastLon;
                hop.Known = true;
            }
        }

        packet.UnknownHopCount = packet.ResolvedPath.Count(e => !e.Known);

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
    string? OriginalMsgId = null);
