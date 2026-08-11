using DireControl.Enums;

namespace DireControl.Api.Controllers.Models;

public sealed class PmsMessageDto
{
    public int Id { get; init; }
    public PmsMessageType Type { get; init; }
    public required string FromCallsign { get; init; }
    public required string ToCallsign { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ReadAt { get; init; }
    public bool IsKilled { get; init; }
    public TerminalSessionOrigin Origin { get; init; }
}

public sealed class PmsMessagePageDto
{
    public required IReadOnlyList<PmsMessageDto> Messages { get; init; }
    public int TotalCount { get; init; }
}

/// <summary>POST api/v0/pms/messages — sysop compose (mail left for RF users).</summary>
public sealed class ComposePmsMessageRequest
{
    public PmsMessageType Type { get; init; } = PmsMessageType.Private;
    public required string ToCallsign { get; init; }
    public string? Subject { get; init; }
    public string? Body { get; init; }
}
