using DireControl.Data.Models;
using DireControl.Enums;

namespace DireControl.Api.Controllers.Models;

public sealed class AboutDto
{
    public required string Version { get; init; }
    public DateTime ServerTime { get; init; }
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
    public HeardVia HeardVia { get; init; }
    public DateTime? LastHeardRf { get; init; }
    public DateTime? LastHeardAprsIs { get; init; }
    public string? LastMode { get; init; }
    public string? LastFrequencyMhz { get; init; }
}

public sealed class PacketDto
{
    public int Id { get; init; }
    public required string StationCallsign { get; init; }
    public DateTime ReceivedAt { get; init; }
    public required string RawPacket { get; init; }
    public PacketType ParsedType { get; init; }
    public PacketSource Source { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string Path { get; init; } = string.Empty;
    public List<ResolvedPathEntry> ResolvedPath { get; init; } = [];
    public int HopCount { get; init; }
    public int UnknownHopCount { get; init; }
    public bool IsDirectHeard { get; init; }
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
    public string? PathUsed { get; init; }
    public DateTime ReceivedAt { get; init; }
    public bool IsRead { get; init; }
    public bool AckSent { get; init; }
    public bool ReplySent { get; init; }
    public int RetryCount { get; init; }
    public int MaxRetries { get; init; }
    public DateTime? NextRetryAt { get; init; }
    public RetryState RetryState { get; init; }
    public DateTime? LastSentAt { get; init; }
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

public sealed class HomePositionDto
{
    public double Lat { get; init; }
    public double Lon { get; init; }
}

public sealed class SettingsDto
{
    public required string OurCallsign { get; init; }
    public HomePositionDto? HomePosition { get; init; }
    public int StationExpiryTimeoutMinutes { get; init; }
    public required string DirewolfHost { get; init; }
    public int DirewolfPort { get; init; }
    public int DirewolfReconnectDelaySeconds { get; init; }
    public int MaxRetryAttempts { get; init; }
    public int InitialRetryDelaySeconds { get; init; }
    public required string OutboundPath { get; init; }

    // APRS-IS settings
    public bool AprsIsEnabled { get; init; }
    public required string AprsIsHost { get; init; }
    public int AprsIsPort { get; init; }
    public int? AprsIsPasscodeOverride { get; init; }
    public int AprsIsPasscodeComputed { get; init; }
    public required string AprsIsFilter { get; init; }
    public int DeduplicationWindowSeconds { get; init; }

    // RF backend / services
    public bool DirewolfEnabled { get; init; }
    public bool DigipeaterEnabled { get; init; }
    public int DigipeaterMaxWideN { get; init; }
    public bool DigipeaterFillInOnly { get; init; }
    public bool KissServerEnabled { get; init; }
    public int KissServerPort { get; init; }
    public bool RfToIsGatingEnabled { get; init; }
    public bool IsToRfGatingEnabled { get; init; }
    public required string IsToRfPath { get; init; }
    public int IsToRfRecentHeardMinutes { get; init; }
}

public sealed class ModemDeviceDto
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool SupportsCapture { get; init; }
    public bool SupportsPlayback { get; init; }
}

public sealed class HidDeviceDto
{
    public required string Path { get; init; }
    public required string Name { get; init; }
}

public sealed class ModemDevicesDto
{
    public required List<ModemDeviceDto> Audio { get; init; }
    public required List<string> SerialPorts { get; init; }
    public required List<HidDeviceDto> HidDevices { get; init; }
}

public sealed class ModemStatusDto
{
    public required string RadioId { get; init; }
    public required string RadioName { get; init; }
    public required string FullCallsign { get; init; }
    public int Channel { get; init; }
    public ModemState State { get; init; }
    public string? CaptureDevice { get; init; }
    public string? ErrorMessage { get; init; }
    public float AudioLevel { get; init; }
    public bool CarrierDetected { get; init; }
    public long DecodedFrames { get; init; }
    public long InvalidFrames { get; init; }
    public bool TxEnabled { get; init; }
    public bool Transmitting { get; init; }
    public long TransmittedFrames { get; init; }
    public long? RigFrequencyHz { get; init; }
    public IReadOnlyDictionary<string, long>? DecodedByProfile { get; init; }
}

public sealed class UpdateStationIdentityRequest
{
    public string? Callsign { get; init; }
    public double? HomeLat { get; init; }
    public double? HomeLon { get; init; }
}

public sealed class UpdateRfServicesRequest
{
    public bool DigipeaterEnabled { get; init; }
    public int DigipeaterMaxWideN { get; init; } = 2;
    public bool DigipeaterFillInOnly { get; init; }
    public bool KissServerEnabled { get; init; }
    public int KissServerPort { get; init; } = 8010;
    public bool RfToIsGatingEnabled { get; init; }
    public bool IsToRfGatingEnabled { get; init; }
    public string IsToRfPath { get; init; } = string.Empty;
    public int IsToRfRecentHeardMinutes { get; init; } = 30;
}

public sealed class UpdateExternalTncRequest
{
    public bool DirewolfEnabled { get; init; }
    public string DirewolfHost { get; init; } = "localhost";
    public int DirewolfPort { get; init; } = 8001;
    public int DirewolfReconnectDelaySeconds { get; init; } = 5;
}

public sealed class UpdateAprsIsSettingsRequest
{
    public bool AprsIsEnabled { get; init; }
    public required string AprsIsHost { get; init; }
    public int AprsIsPort { get; init; }
    public int? AprsIsPasscodeOverride { get; init; }
    public required string AprsIsFilter { get; init; }
    public int DeduplicationWindowSeconds { get; init; }
}

public sealed class UpdateWeatherApiKeysRequest
{
    public string? OpenWeatherMapApiKey { get; init; }
    public string? TomorrowIoApiKey { get; init; }
    public RadarProvider? RadarProvider { get; init; }
    public string? RainViewerProApiKey { get; init; }
}

// ─── Weather proxy DTOs ────────────────────────────────────────────────────

public sealed class WeatherManifestDto
{
    public long Generated { get; init; }
    public int MaxNativeZoom { get; init; }
    public int TileSize { get; init; }
    public required WeatherRadarManifestDto Radar { get; init; }
}

public sealed class WeatherRadarManifestDto
{
    public required List<WeatherFrameDto> Past { get; init; }
    public required List<WeatherFrameDto> Nowcast { get; init; }
}

public sealed class WeatherFrameDto
{
    public long Time { get; init; }
    public required string Path { get; init; }
}

public sealed class WeatherStatusDto
{
    public required WeatherLayerStatusDto Radar { get; init; }
    public required WeatherLayerStatusDto Wind { get; init; }
    public required WeatherLayerStatusDto Lightning { get; init; }
    public RadarProvider RadarProvider { get; init; }
    public bool RainViewerProKeyConfigured { get; init; }
}

public sealed class WeatherLayerStatusDto
{
    public bool Available { get; init; }
    public int? FrameCount { get; init; }
    public DateTime? LastUpdated { get; init; }
    public string? Reason { get; init; }
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
    public int Id { get; init; }
    public required string Callsign { get; init; }
    public required string ParsedType { get; init; }
    public PacketSource Source { get; init; }
    public DateTime ReceivedAt { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public required string Summary { get; init; }
    public int HopCount { get; init; }
    public List<ResolvedPathEntry> ResolvedPath { get; init; } = [];
}

public sealed class SendMessageRequest
{
    public required string ToCallsign { get; init; }
    public required string Body { get; init; }
    /// <summary>
    /// Optional per-message VIA path override. When null or empty the
    /// configured default outbound path is used.
    /// </summary>
    public string? Path { get; init; }
}

public sealed class UpdateOutboundPathRequest
{
    public string OutboundPath { get; init; } = string.Empty;
}

public sealed class MessageAckDto
{
    public int Id { get; init; }
    public required string MessageId { get; init; }
}

public sealed class MessageRetriedDto
{
    public int Id { get; init; }
    public int RetryCount { get; init; }
    public int MaxRetries { get; init; }
    public DateTime? NextRetryAt { get; init; }
    public DateTime? LastSentAt { get; init; }
}

public sealed class MessageAcknowledgedDto
{
    public int Id { get; init; }
    public required string MessageId { get; init; }
}

public sealed class MessageFailedDto
{
    public int Id { get; init; }
    public required string ToCallsign { get; init; }
    public int RetryCount { get; init; }
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
    public long DigipeatedFrames { get; init; }
    public int KissServerClients { get; init; }
    public long RfToIsGatedLines { get; init; }
    public required string AprsIsState { get; init; }
    public string? AprsIsServerName { get; init; }
    public string AprsIsFilter { get; init; } = string.Empty;
    public long AprsIsSessionPacketCount { get; init; }
    public DateTime? AprsIsFirstDisconnectedAt { get; init; }
    public DateTime? AprsIsLastConnectAttemptAt { get; init; }
    public int AprsIsFailedAttempts { get; init; }
    public string? AprsIsLastError { get; init; }
    public ModemState ModemState { get; init; }
    public bool ModemCarrierDetected { get; init; }
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

public sealed class StationFrequencyDto
{
    public required string Callsign { get; init; }
    public required string FrequencyMhz { get; init; }
    public string? Mode { get; init; }
    public StationType StationType { get; init; }
    public DateTime LastSeen { get; init; }
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

// ─── Radio management ────────────────────────────────────────────────────────

/// <summary>Per-radio sound modem + PTT configuration (audio feed).</summary>
public sealed class RadioModemConfigDto
{
    public bool ModemEnabled { get; init; }
    public string ModemCaptureDevice { get; init; } = "default";
    public string ModemPlaybackDevice { get; init; } = "default";
    public bool TxEnabled { get; init; }
    public int TxAudioLevelPct { get; init; } = 80;
    public int TxDelayMs { get; init; } = 300;
    public int TxTailMs { get; init; } = 50;
    public int TxPersistence { get; init; } = 63;
    public int TxSlotTimeMs { get; init; } = 100;
    public PttMethod PttMethod { get; init; } = PttMethod.None;
    public string? PttSerialPort { get; init; }
    public bool PttSerialUseRts { get; init; } = true;
    public bool PttSerialUseDtr { get; init; }
    public string? PttHidDevice { get; init; }
    public int PttHidPin { get; init; } = 3;
    public int PttGpioChip { get; init; }
    public int PttGpioLine { get; init; }
    public bool PttGpioActiveLow { get; init; }
    public string PttRigctldHost { get; init; } = "localhost";
    public int PttRigctldPort { get; init; } = 4532;
}

public sealed class RadioDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Callsign { get; init; }
    public string? Ssid { get; init; }
    public required string FullCallsign { get; init; }
    public int ChannelNumber { get; init; }
    public string? Notes { get; init; }
    public string? BeaconPath { get; init; }
    public string? BeaconSymbol { get; init; }
    public string? BeaconComment { get; init; }
    public bool IsActive { get; init; }
    public int ExpectedIntervalSeconds { get; init; }
    public bool AutoBeaconEnabled { get; init; }
    public int AutoBeaconIntervalSeconds { get; init; }
    public double? FrequencyMhz { get; init; }
    public string? Mode { get; init; }
    public DateTime? LastBeaconedAt { get; init; }
    public int? SecondsSinceBeacon { get; init; }
    public int ConfirmationCount { get; init; }
    public int BeaconCount { get; init; }
    public required RadioModemConfigDto Modem { get; init; }
}

public sealed class CreateRadioRequest
{
    public required string Name { get; init; }
    public required string Callsign { get; init; }
    public string? Ssid { get; init; }
    public int ChannelNumber { get; init; } = 0;
    public string? Notes { get; init; }
    public string? BeaconPath { get; init; }
    public string? BeaconSymbol { get; init; }
    public string? BeaconComment { get; init; }
    public int ExpectedIntervalSeconds { get; init; } = 600;
    public bool AutoBeaconEnabled { get; init; }
    public int AutoBeaconIntervalSeconds { get; init; } = 1800;
    public double? FrequencyMhz { get; init; }
    public string? Mode { get; init; }
    public RadioModemConfigDto? Modem { get; init; }
}

public sealed class UpdateRadioRequest
{
    public required string Name { get; init; }
    public required string Callsign { get; init; }
    public string? Ssid { get; init; }
    public int ChannelNumber { get; init; }
    public string? Notes { get; init; }
    public string? BeaconPath { get; init; }
    public string? BeaconSymbol { get; init; }
    public string? BeaconComment { get; init; }
    public int ExpectedIntervalSeconds { get; init; }
    public bool AutoBeaconEnabled { get; init; }
    public int AutoBeaconIntervalSeconds { get; init; }
    public double? FrequencyMhz { get; init; }
    public string? Mode { get; init; }
    public RadioModemConfigDto? Modem { get; init; }
}

/// <summary>Live TX audio-level (gain) adjustment for a single radio.</summary>
public sealed class SetTxLevelRequest
{
    public int TxAudioLevelPct { get; init; }
}

/// <summary>A TX calibration test tone to transmit on a single radio.</summary>
public sealed class TestToneRequest
{
    public TestToneKind Kind { get; init; } = TestToneKind.Mark;
    public int DurationMs { get; init; } = 2000;
}

public sealed class DigiConfirmationDto
{
    public required string Digipeater { get; init; }
    public DateTime ConfirmedAt { get; init; }
    public int SecondsAfterBeacon { get; init; }
    public double? Lat { get; init; }
    public double? Lon { get; init; }
    public string? AliasUsed { get; init; }
}

public sealed class LastBeaconDto
{
    public required string RadioId { get; init; }
    public required string RadioName { get; init; }
    public required string FullCallsign { get; init; }
    public DateTime? BeaconedAt { get; init; }
    public int? SecondsSinceBeacon { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? PathUsed { get; init; }
    public string? Comment { get; init; }
    public bool Heard { get; init; }
    public IReadOnlyList<DigiConfirmationDto> Confirmations { get; init; } = [];
}

public sealed class OwnBeaconHistoryItemDto
{
    public int Id { get; init; }
    public DateTime BeaconedAt { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? PathUsed { get; init; }
    public int HopCount { get; init; }
    public bool Heard { get; init; }
    public IReadOnlyList<DigiConfirmationDto> Confirmations { get; init; } = [];
}

public sealed class OwnBeaconBroadcastDto
{
    public required string RadioId { get; init; }
    public int BeaconId { get; init; }
    public required string FullCallsign { get; init; }
    public DateTime BeaconedAt { get; init; }
    public double? Lat { get; init; }
    public double? Lon { get; init; }
    public string? PathUsed { get; init; }
    public bool Heard { get; init; }
}

public sealed class BeaconConfirmedHeardDto
{
    public required string RadioId { get; init; }
    public int BeaconId { get; init; }
}

public sealed class DigiConfirmationBroadcastDto
{
    public required string RadioId { get; init; }
    public int BeaconId { get; init; }
    public required string FullCallsign { get; init; }
    public required string Digipeater { get; init; }
    public DateTime ConfirmedAt { get; init; }
    public int SecondsAfterBeacon { get; init; }
    public double? Lat { get; init; }
    public double? Lon { get; init; }
}
