using DireControl.Enums;

namespace DireControl.Api.Controllers.Models;

public sealed class TerminalSessionStatsDto
{
    public int Vs { get; init; }
    public int Vr { get; init; }
    public int Va { get; init; }
    public int OutstandingIFrames { get; init; }
    public int RetryCount { get; init; }
    public int SendQueueDepth { get; init; }
    public long BytesIn { get; init; }
    public long BytesOut { get; init; }
}

public sealed class TerminalSessionDto
{
    public required string Id { get; init; }
    public TerminalSessionOrigin Origin { get; init; }
    public TerminalSessionState State { get; init; }
    public int Channel { get; init; }
    public required string LocalCallsign { get; init; }
    public required string RemoteCallsign { get; init; }
    public required string DigiPath { get; init; }
    public DateTime StartedAt { get; init; }
    public string? EndReason { get; init; }
    public required TerminalSessionStatsDto Stats { get; init; }
}

/// <summary>POST api/v0/terminal/sessions — open an outbound session.</summary>
public sealed class OpenTerminalSessionRequest
{
    public int Channel { get; init; }
    public required string RemoteCallsign { get; init; }

    /// <summary>Local callsign override; null = station callsign.</summary>
    public string? LocalCallsign { get; init; }

    /// <summary>Comma-separated digipeater path; empty/null = direct.</summary>
    public string? DigiPath { get; init; }

    // LAPB overrides; null falls back to the configured defaults.
    public bool? Mod128 { get; init; }
    public int? PacLen { get; init; }
    public int? WindowSize { get; init; }
    public int? MaxRetries { get; init; }
    public int? T1Seconds { get; init; }
}

public sealed class TerminalTranscriptSummaryDto
{
    public int Id { get; init; }
    public required string SessionId { get; init; }
    public TerminalSessionOrigin Origin { get; init; }
    public int Channel { get; init; }
    public required string LocalCallsign { get; init; }
    public required string RemoteCallsign { get; init; }
    public required string DigiPath { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public string? EndReason { get; init; }
    public long BytesIn { get; init; }
    public long BytesOut { get; init; }
}

public sealed class TerminalTranscriptChunkDto
{
    public DateTime Timestamp { get; init; }
    public TranscriptDirection Direction { get; init; }
    public required string DataBase64 { get; init; }
}

public sealed class TerminalTranscriptDetailDto
{
    public required TerminalTranscriptSummaryDto Summary { get; init; }
    public required IReadOnlyList<TerminalTranscriptChunkDto> Chunks { get; init; }
}

public sealed class TerminalPresetDto
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public required string RemoteCallsign { get; init; }
    public int Channel { get; init; }
    public required string DigiPath { get; init; }
    public string? LocalCallsign { get; init; }
    public bool IsPinned { get; init; }
    public DateTime LastUsedAt { get; init; }
    public int UseCount { get; init; }
    public bool? Mod128 { get; init; }
    public int? PacLen { get; init; }
    public int? WindowSize { get; init; }
    public int? MaxRetries { get; init; }
    public int? T1Seconds { get; init; }
}

public sealed class SaveTerminalPresetRequest
{
    public string? Name { get; init; }
    public required string RemoteCallsign { get; init; }
    public int Channel { get; init; }
    public string? DigiPath { get; init; }
    public string? LocalCallsign { get; init; }
    public bool IsPinned { get; init; }
    public bool? Mod128 { get; init; }
    public int? PacLen { get; init; }
    public int? WindowSize { get; init; }
    public int? MaxRetries { get; init; }
    public int? T1Seconds { get; init; }
}

public sealed class TerminalMacroDto
{
    public int Id { get; init; }
    public required string Label { get; init; }
    public int SortOrder { get; init; }
    public required string PayloadBase64 { get; init; }
    public bool AppendCr { get; init; }
}

public sealed class SaveTerminalMacroRequest
{
    public required string Label { get; init; }
    public int SortOrder { get; init; }
    public required string PayloadBase64 { get; init; }
    public bool AppendCr { get; init; } = true;
}
