using DireControl.Data.Models;
using DireControl.Enums;

namespace DireControl.Api.Controllers.Models;

public sealed class AboutDto
{
    public required string Version { get; init; }
}

public sealed class StationDto
{
    public required string Callsign { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }
    public double? LastLat { get; init; }
    public double? LastLon { get; init; }
    public int? LastHeading { get; init; }
    public double? LastSpeed { get; init; }
    public double? LastAltitude { get; init; }
    public required string Symbol { get; init; }
    public string Status { get; init; } = string.Empty;
    public bool IsWeatherStation { get; init; }
    public StationType StationType { get; init; }
    public QrzLookupData? QrzLookupData { get; init; }
    public bool IsOnWatchList { get; init; }
    public string? GridSquare { get; init; }
}

public sealed class PacketDto
{
    public int Id { get; init; }
    public required string StationCallsign { get; init; }
    public DateTime ReceivedAt { get; init; }
    public required string RawPacket { get; init; }
    public PacketType ParsedType { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string Path { get; init; } = string.Empty;
    public List<ResolvedPathEntry> ResolvedPath { get; init; } = [];
    public int HopCount { get; init; }
    public string Comment { get; init; } = string.Empty;
    public WeatherData? WeatherData { get; init; }
    public TelemetryData? TelemetryData { get; init; }
    public MessageData? MessageData { get; init; }
    public SignalData? SignalData { get; init; }
    public string? GridSquare { get; init; }
}

public sealed class InboxMessageDto
{
    public int Id { get; init; }
    public required string FromCallsign { get; init; }
    public required string ToCallsign { get; init; }
    public string Body { get; init; } = string.Empty;
    public string MessageId { get; init; } = string.Empty;
    public DateTime ReceivedAt { get; init; }
    public bool IsRead { get; init; }
    public bool AckSent { get; init; }
    public bool ReplySent { get; init; }
}

public sealed class AllMessagePacketDto
{
    public int PacketId { get; init; }
    public required string FromCallsign { get; init; }
    public string ToCallsign { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string? MessageId { get; init; }
    public DateTime ReceivedAt { get; init; }
    public required string RawPacket { get; init; }
}

public sealed class PaginatedResponse<T>
{
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public IReadOnlyList<T> Items { get; init; } = [];
}

public sealed class SettingsDto
{
    public required string OurCallsign { get; init; }
    public double? StationLatitude { get; init; }
    public double? StationLongitude { get; init; }
    public int StationExpiryTimeoutMinutes { get; init; }
    public required string DirewolfHost { get; init; }
    public int DirewolfPort { get; init; }
    public int DirewolfReconnectDelaySeconds { get; init; }
}

public sealed class TrackPointDto
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public DateTime ReceivedAt { get; init; }
    public double? Speed { get; init; }
    public int? Heading { get; init; }
}

public sealed class WeatherReadingDto
{
    public DateTime ReceivedAt { get; init; }
    public double? Temperature { get; init; }
    public int? Humidity { get; init; }
    public double? WindSpeed { get; init; }
    public int? WindDirection { get; init; }
    public double? WindGust { get; init; }
    public double? Pressure { get; init; }
    public double? RainLastHour { get; init; }
    public double? RainLast24h { get; init; }
    public double? RainSinceMidnight { get; init; }
}

public sealed class PacketBroadcastDto
{
    public required string Callsign { get; init; }
    public required string ParsedType { get; init; }
    public DateTime ReceivedAt { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public required string Summary { get; init; }
}

public sealed class SendMessageRequest
{
    public required string ToCallsign { get; init; }
    public required string Body { get; init; }
}

public sealed class MessageAckDto
{
    public int Id { get; init; }
    public required string MessageId { get; init; }
}

public sealed class AlertDto
{
    public int Id { get; init; }
    public int AlertType { get; init; }
    public required string AlertTypeName { get; init; }
    public required string Callsign { get; init; }
    public DateTime TriggeredAt { get; init; }
    public bool IsAcknowledged { get; init; }
    public double? DistanceMeters { get; init; }
    public string? GeofenceName { get; init; }
    public string? Direction { get; init; }
    public string? RuleName { get; init; }
    public string? MessageText { get; init; }
}

public sealed class AlertBroadcastDto
{
    public int Id { get; init; }
    public required string AlertTypeName { get; init; }
    public required string Callsign { get; init; }
    public DateTime TriggeredAt { get; init; }
    public string? GeofenceName { get; init; }
    public string? Direction { get; init; }
    public string? RuleName { get; init; }
    public double? DistanceMeters { get; init; }
}

public sealed class GeofenceDto
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public double CenterLat { get; init; }
    public double CenterLon { get; init; }
    public double RadiusMeters { get; init; }
    public bool IsActive { get; init; }
    public bool AlertOnEnter { get; init; }
    public bool AlertOnExit { get; init; }
}

public sealed class CreateGeofenceRequest
{
    public required string Name { get; init; }
    public double CenterLat { get; init; }
    public double CenterLon { get; init; }
    public double RadiusMeters { get; init; }
    public bool AlertOnEnter { get; init; } = true;
    public bool AlertOnExit { get; init; } = true;
}

public sealed class ProximityRuleDto
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public string? TargetCallsign { get; init; }
    public double CenterLat { get; init; }
    public double CenterLon { get; init; }
    public double RadiusMetres { get; init; }
    public bool IsActive { get; init; }
}

public sealed class CreateProximityRuleRequest
{
    public required string Name { get; init; }
    public string? TargetCallsign { get; init; }
    public double CenterLat { get; init; }
    public double CenterLon { get; init; }
    public double RadiusMetres { get; init; }
}

public sealed class CallsignLookupDto
{
    public string? Name { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? LicenseClass { get; init; }
    public string? GridSquare { get; init; }
}

public sealed class StationStatisticDto
{
    public int PacketsToday { get; init; }
    public int PacketsAllTime { get; init; }
    public double AveragePacketsPerHour { get; init; }
    public int LongestGapMinutes { get; init; }
    public int[] PacketsPerHour { get; init; } = new int[24];
}

public sealed class StatisticsDto
{
    public int PacketsToday { get; init; }
    public int UniqueStationsToday { get; init; }
    public int UniqueStationsThisWeek { get; init; }
    public int UniqueStationsAllTime { get; init; }
    public int[] PacketsPerHour { get; init; } = new int[24];
    public IReadOnlyList<CallsignCountDto> BusiestDigipeaters { get; init; } = [];
    public IReadOnlyList<CallsignCountDto> BusiestStations { get; init; } = [];
    public IReadOnlyList<RecentlyHeardDto> RecentlyFirstHeard { get; init; } = [];
    public IReadOnlyList<string> GridSquares { get; init; } = [];
}

public sealed class CallsignCountDto
{
    public required string Callsign { get; init; }
    public int Count { get; init; }
    public double AveragePerHour { get; init; }
}

public sealed class RecentlyHeardDto
{
    public required string Callsign { get; init; }
    public DateTime FirstSeen { get; init; }
    public StationType StationType { get; init; }
}

public sealed class StatusDto
{
    public bool DirewolfConnected { get; init; }
    public bool ApiOnline { get; init; } = true;
}

public sealed class SignalPointDto
{
    public DateTime ReceivedAt { get; init; }
    public int? DecodeQuality { get; init; }
    public double? FrequencyOffsetHz { get; init; }
}

public sealed class DigipeaterAnalysisEntryDto
{
    public required string Callsign { get; init; }
    public int TotalPacketsForwarded { get; init; }
    public int Last24h { get; init; }
    public double AverageHopsFromUs { get; init; }
}

public sealed class PacketPositionDto
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
}

public sealed class CoverageGridSquareDto
{
    public required string GridSquare { get; init; }
    public double Lat { get; init; }
    public double Lon { get; init; }
    public int PacketCount { get; init; }
}
