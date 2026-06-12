using System.Text.RegularExpressions;
using AprsSharp.AprsParser;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using DireControl.PathParsing;
using Microsoft.EntityFrameworkCore;
using OurPacketType = DireControl.Enums.PacketType;
using DbPacket = DireControl.Data.Models.Packet;

namespace DireControl.Api.Services;

/// <summary>
/// Pure packet-decoding core: derives all of a packet's structured fields (and the
/// associated <see cref="Station"/> mutations) from its raw TNC2 string, collecting
/// messaging side-effects into a <see cref="MessageEffect"/> list for the caller to
/// apply. Holds no service state so it can be unit-tested with an in-memory SQLite
/// context, without constructing <see cref="AprsPacketParsingService"/>.
/// Station lookups go through <c>db.Stations.Local</c> first — callers are expected
/// to preload the relevant stations (senders + path hops) for the batch.
/// </summary>
internal static class PacketDecoder
{
    /// <summary>
    /// Minimum position delta (decimal degrees) treated as meaningful movement.
    /// ~111 m at the equator; generous enough to absorb GPS jitter.
    /// </summary>
    internal const double MovementThresholdDeg = 0.001;

    /// <summary>
    /// Two-character APRS symbol strings (table + code) that unambiguously represent
    /// a mobile platform.  A match immediately classifies the transmitting station
    /// as <see cref="StationType.Mobile"/> unless a higher-priority type is already set.
    /// </summary>
    private static readonly HashSet<string> MobileSymbols = new(StringComparer.Ordinal)
    {
        // Primary table (/)
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
        // Alternate table (\)
        "\\>",  // Car
        "\\j",  // Jeep
        "\\k",  // Truck
        "\\u",  // Bus
    };

    internal static async Task DecodeAsync(
        DbPacket packet,
        DireControlContext db,
        string ourCallsign,
        List<MessageEffect> effects,
        CancellationToken ct,
        bool reprocess = false,
        IReadOnlyList<Radio>? activeRadios = null)
    {
        activeRadios ??= [];
        var aprs = new AprsSharp.AprsParser.Packet(packet.RawPacket);

        packet.ParsedType = MapPacketType(aprs.InfoField);

        // Extract path from the raw TNC2 string.  ParseTnc2Header reads directly
        // from RawPacket so asterisk markers from the AX.25 H-bit are preserved;
        // the returned RawPath already excludes the TOCALL.
        var (source, tocall, rawPath) = AprsPathParser.ParseTnc2Header(packet.RawPacket);

        // When reprocessing, re-derive StationCallsign from the TNC2 header (the outer
        // source) to repair rows whose value was corrupted by older parsers. The live
        // path leaves StationCallsign exactly as ingest stored it (the preloaded Station
        // and FK depend on it), so live behaviour is unchanged.
        if (reprocess && !string.IsNullOrWhiteSpace(source) && packet.RawPacket.Contains('>'))
            packet.StationCallsign = source;

        packet.ParserVersion = ParserVersionInfo.Current;

        packet.Path = rawPath;   // e.g. "WE4MB-3*,WIDE2" — TOCALL absent, asterisks intact

        List<string> pathList = new List<string> { tocall };
        if (!string.IsNullOrEmpty(rawPath))
        {
            pathList.AddRange(rawPath.Split(',', StringSplitOptions.RemoveEmptyEntries));
        }

        // Full ResolvedPath (with source + home + coordinates) is built in ResolvePathCoordinatesAsync.
        var (viaHops, hopCount) = AprsPathParser.ExtractViaHops(pathList);
        packet.HopCount = hopCount;
        packet.ResolvedPath = viaHops;

        switch (aprs.InfoField)
        {
            case WeatherInfo weather:
                HandleWeather(packet, weather, db, reprocess);
                break;

            case PositionlessWeatherInfo plWx:
                HandlePositionlessWeather(packet, plWx, db, reprocess);
                break;

            case MicEInfo micE:
                HandleMicE(packet, micE, db, reprocess);
                break;

            case ObjectInfo obj:
                HandleObjectOrItem(packet, obj.Comment, obj.Position);
                break;

            case ItemInfo item:
                HandleObjectOrItem(packet, item.Comment, item.Position);
                break;

            case PositionInfo position:
                HandlePosition(packet, position, db, reprocess);
                break;

            case MessageInfo message:
                await HandleMessageAsync(packet, message, db, ourCallsign, activeRadios, effects, ct, reprocess: reprocess);
                break;

            case StatusInfo status:
                packet.Comment = status.Comment ?? string.Empty;
                if (!reprocess && !string.IsNullOrWhiteSpace(packet.Comment))
                    UpdateStation(db, packet.StationCallsign, s => s.Status = packet.Comment);
                break;

            case TelemetryInfo telemetry:
                packet.TelemetryData = new TelemetryData
                {
                    SequenceNumber = telemetry.SequenceNumber?.ToString(),
                    Analogs = telemetry.AnalogValues?.Select(a => (double?)a).ToArray(),
                    Digitals = telemetry.DigitalBits,
                    Comment = telemetry.Comment,
                };
                break;

            case ThirdPartyTrafficInfo thirdParty:
                await HandleThirdPartyAsync(packet, thirdParty, db, ourCallsign, activeRadios, effects, ct, reprocess);
                break;
        }

        // Maidenhead grid square — derived for any packet that carries a position.
        // The packet field is re-derived on reprocess; the station field is live-only.
        if (packet.Latitude is { } gsLat && packet.Longitude is { } gsLon)
        {
            packet.GridSquare = MaidenheadLocator.FromLatLon(gsLat, gsLon);
            if (!reprocess && packet.GridSquare is not null)
                UpdateStation(db, packet.StationCallsign, s => s.GridSquare = packet.GridSquare);
        }

        // Mode / frequency / altitude / gateway detection
        // Skipped when reprocessing: these mutate live Station state from the packet,
        // and replaying historical packets must not overwrite a station's current mode.
        var mode = DetectMode(tocall, packet.Comment);
        var freq = ParseFrequency(packet.Comment);
        var altitude = ParseAltitudeFeet(packet.Comment);

        if (!reprocess && (mode != null || freq != null || altitude != null))
        {
            UpdateStation(db, packet.StationCallsign, s =>
            {
                if (mode != null) s.LastMode = mode;
                if (freq != null) s.LastFrequencyMhz = freq;
                if (altitude != null) s.LastAltitude = altitude;

                if (IsGatewayTocall(tocall) &&
                    s.StationType is StationType.Unknown or StationType.Fixed)
                {
                    s.StationType = StationType.Gateway;
                }
            });
        }
    }

    private static void HandlePosition(DbPacket packet, PositionInfo info, DireControlContext db, bool reprocess = false)
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

            // Reprocessing must not replay this packet's position/symbol onto the live
            // Station record — only the packet's own fields above are re-derived.
            if (reprocess)
                return;

            var symbol = $"{pos.SymbolTableIdentifier}{pos.SymbolCode}";

            UpdateStation(db, packet.StationCallsign, station =>
            {
                // Mobile detection
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
                    && (Math.Abs(coord.Latitude - station.LastLat.Value) > MovementThresholdDeg
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

    private static void HandleWeather(DbPacket packet, WeatherInfo info, DireControlContext db, bool reprocess = false)
    {
        HandlePosition(packet, info, db, reprocess);

        // Rainfall fields in AprsSharp are in 100ths of an inch; convert to inches.
        // Pressure in AprsSharp is in tenths of mb; convert to mb.
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

        if (reprocess)
            return;

        UpdateStation(db, packet.StationCallsign, s =>
        {
            s.IsWeatherStation = true;
            s.StationType = StationType.Weather;
        });
    }

    private static void HandlePositionlessWeather(DbPacket packet, PositionlessWeatherInfo info, DireControlContext db, bool reprocess = false)
    {
        packet.Comment = info.Comment ?? string.Empty;
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

        if (reprocess)
            return;

        UpdateStation(db, packet.StationCallsign, s =>
        {
            s.IsWeatherStation = true;
            s.StationType = StationType.Weather;
        });
    }

    private static void HandleMicE(DbPacket packet, MicEInfo info, DireControlContext db, bool reprocess = false)
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

            // Reprocessing must not replay this packet's position/symbol onto the live
            // Station record — only the packet's own fields above are re-derived.
            if (reprocess)
                return;

            var symbol = $"{pos.SymbolTableIdentifier}{pos.SymbolCode}";

            UpdateStation(db, packet.StationCallsign, station =>
            {
                var isMobileSymbol = MobileSymbols.Contains(symbol);
                var hasMoved = station.LastLat is not null
                    && station.LastLon is not null
                    && !double.IsNaN(coord.Latitude)
                    && !double.IsNaN(coord.Longitude)
                    && (Math.Abs(coord.Latitude - station.LastLat.Value) > MovementThresholdDeg
                     || Math.Abs(coord.Longitude - station.LastLon.Value) > MovementThresholdDeg);

                if ((isMobileSymbol || hasMoved) &&
                    station.StationType is StationType.Unknown or StationType.Fixed)
                {
                    station.StationType = StationType.Mobile;
                }

                if (!double.IsNaN(coord.Latitude)) station.LastLat = coord.Latitude;
                if (!double.IsNaN(coord.Longitude)) station.LastLon = coord.Longitude;
                if (info.Course is { } heading) station.LastHeading = heading;
                if (info.Speed is { } speed) station.LastSpeed = speed * 1.15078; // knots → mph
                station.Symbol = symbol;
            });
        }
    }

    private static void HandleObjectOrItem(DbPacket packet, string? comment, Position? position)
    {
        packet.Comment = comment ?? string.Empty;

        if (position is { } pos)
        {
            var coord = pos.Coordinates;
            if (!double.IsNaN(coord.Latitude) && !double.IsNaN(coord.Longitude))
            {
                packet.Latitude = coord.Latitude;
                packet.Longitude = coord.Longitude;
            }
        }
    }

    private static async Task HandleThirdPartyAsync(
        DbPacket packet,
        ThirdPartyTrafficInfo info,
        DireControlContext db,
        string ourCallsign,
        IReadOnlyList<Radio> activeRadios,
        List<MessageEffect> effects,
        CancellationToken ct,
        bool reprocess = false)
    {
        if (info.InnerPacket?.InfoField is MessageInfo innerMsg)
        {
            packet.ParsedType = OurPacketType.Message;
            var innerSender = info.InnerPacket.Sender ?? packet.StationCallsign;
            await HandleMessageAsync(packet, innerMsg, db, ourCallsign, activeRadios, effects, ct,
                senderCallsignOverride: innerSender, reprocess: reprocess);
        }
    }

    private static async Task HandleMessageAsync(
        DbPacket packet,
        MessageInfo info,
        DireControlContext db,
        string ourCallsign,
        IReadOnlyList<Radio> activeRadios,
        List<MessageEffect> effects,
        CancellationToken ct,
        string? senderCallsignOverride = null,
        bool reprocess = false)
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

        // Everything below mutates external state (inbox rows, auto-ACK/send effects).
        // When reprocessing historical packets we only re-derive the row's fields above
        // and must not replay those side effects.
        if (reprocess)
            return;

        if (string.IsNullOrWhiteSpace(ourCallsign))
            return;

        // A message is "for us" when addressed to the primary callsign or to any
        // active radio's callsign (-0 SSID equivalence handled by the matcher).
        var addresseeCallsign = addressee.Trim();
        var isForUs = addresseeCallsign.Equals(ourCallsign, StringComparison.OrdinalIgnoreCase)
            || activeRadios.Any(r => CallsignMatcher.Matches(r, addresseeCallsign));
        if (!isForUs)
            return;

        // Detect ACK receipts: body is "ackXXXX" where XXXX is the original message ID.
        if (MessageHandlingLogic.TryParseAck(body, out var originalMsgId))
        {
            effects.Add(new MessageEffect(
                IsNewInboxMessage: false,
                IsAckReceived: true,
                PeerCallsign: fromCallsign,
                MessageId: messageId,
                OriginalMsgId: originalMsgId,
                AddresseeCallsign: addresseeCallsign));
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
                    IsDuplicateInboxMessage: true,
                    AddresseeCallsign: addresseeCallsign));
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
        });

        if (!string.IsNullOrWhiteSpace(messageId))
        {
            effects.Add(new MessageEffect(
                IsNewInboxMessage: true,
                IsAckReceived: false,
                PeerCallsign: fromCallsign,
                MessageId: messageId,
                AddresseeCallsign: addresseeCallsign));
        }
    }

    /// <summary>
    /// Builds the complete <see cref="DbPacket.ResolvedPath"/> for a packet:
    /// <list type="bullet">
    ///   <item>HopIndex 0 — originating station (position from packet or Station table)</item>
    ///   <item>HopIndex 1…n-1 — intermediate digipeater hops (already populated by DecodeAsync)</item>
    ///   <item>HopIndex n — our own station (home position from settings)</item>
    /// </list>
    /// </summary>
    internal static async Task ResolvePathCoordinatesAsync(
        DbPacket packet,
        DireControlContext db,
        string ourCallsign,
        double? homeLat,
        double? homeLon,
        CancellationToken ct,
        bool reprocess = false)
    {
        // Hop 0: originating station
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

        // Intermediate hops (already extracted by DecodeAsync, HopIndex 1+)
        foreach (var hop in packet.ResolvedPath)
        {
            if (AprsPathParser.IsGenericAlias(hop.Callsign))
            {
                hop.Known = false;
                continue;
            }

            var station = db.Stations.Local.FirstOrDefault(s => s.Callsign == hop.Callsign)
                ?? await db.Stations.FindAsync(new object?[] { hop.Callsign }, ct);

            // Classify the station based on its role in this packet's path.
            // Skipped during reprocessing — promoting station types from replayed
            // historical packets would mutate current station state.
            if (!reprocess && station != null)
            {
                if (hop.IsIgate &&
                    station.StationType is StationType.Unknown or StationType.Digipeater)
                {
                    station.StationType = StationType.IGate;
                }
                else if (!hop.IsIgate && station.StationType == StationType.Unknown)
                {
                    station.StationType = StationType.Digipeater;
                }
            }

            if (station?.LastLat != null && station.LastLon != null)
            {
                hop.Latitude = station.LastLat;
                hop.Longitude = station.LastLon;
                hop.Known = true;
            }
        }

        packet.UnknownHopCount = packet.ResolvedPath.Count(e => !e.Known);

        // Final hop: our own station
        var homeEntry = new ResolvedPathEntry
        {
            Callsign = ourCallsign,
            Latitude = homeLat,
            Longitude = homeLon,
            Known = homeLat != null && homeLon != null,
            HopIndex = packet.ResolvedPath.Count + 1,
        };

        // Prepend source and append home so the list is fully ordered
        packet.ResolvedPath.Insert(0, sourceEntry);
        packet.ResolvedPath.Add(homeEntry);
    }

    private static void UpdateStation(DireControlContext db, string callsign, Action<Station> update)
    {
        var station = db.Stations.Local.FirstOrDefault(s => s.Callsign == callsign);
        if (station is not null)
            update(station);
    }

    internal static OurPacketType MapPacketType(InfoField? infoField) => infoField switch
    {
        WeatherInfo => OurPacketType.Weather,
        PositionlessWeatherInfo => OurPacketType.Weather,
        MicEInfo => OurPacketType.Position,
        PositionInfo => OurPacketType.Position,
        ObjectInfo => OurPacketType.Object,
        ItemInfo => OurPacketType.Item,
        MessageInfo => OurPacketType.Message,
        StatusInfo => OurPacketType.Status,
        TelemetryInfo => OurPacketType.Telemetry,
        ThirdPartyTrafficInfo => OurPacketType.Unparseable, // overridden if inner packet is a message
        _ => OurPacketType.Unparseable,
    };

    internal static string BuildSummary(DbPacket packet)
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

    private static readonly Regex FrequencyRegex = new(
        @"(\d{2,3}\.\d{3,5})\s*MHz",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // APRS101 comment-text altitude: "/A=nnnnnn" in feet (sign tolerated).
    private static readonly Regex AltitudeRegex = new(
        @"/A=(-?\d{4,6})",
        RegexOptions.Compiled);

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

    /// <summary>
    /// Extracts the APRS comment-text altitude (<c>/A=nnnnnn</c>, feet).
    /// Returns <see langword="null"/> when the comment carries no altitude.
    /// </summary>
    internal static double? ParseAltitudeFeet(string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return null;
        var match = AltitudeRegex.Match(comment);
        return match.Success && double.TryParse(match.Groups[1].Value, out var feet) ? feet : null;
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
    bool IsDuplicateInboxMessage = false,
    string? AddresseeCallsign = null);
