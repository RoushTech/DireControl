namespace DireControl.Api.Controllers.Models;

/// <summary>
/// A single application log record streamed to the frontend log viewer over
/// SignalR (see <see cref="DireControl.Api.Hubs.LogHub"/>). <see cref="Sequence"/>
/// is a monotonically increasing id used by the client to de-duplicate the
/// connect-time backlog against live entries and to key list rows.
/// </summary>
public sealed class LogEntryDto
{
    public required long Sequence { get; init; }

    /// <summary>UTC timestamp captured when the entry was logged.</summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>Log level name, e.g. "Information", "Warning", "Error".</summary>
    public required string Level { get; init; }

    /// <summary>Full logger category, e.g. "DireControl.Api.Services.KissTcpService".</summary>
    public required string Category { get; init; }

    public required string Message { get; init; }

    /// <summary>Formatted exception (message + stack trace) when one was logged.</summary>
    public string? Exception { get; init; }
}
