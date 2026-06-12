using DireControl.Api.Services;

namespace DireControl.Api.Controllers.Models;

/// <summary>Current maintenance state: DB size, retention windows, schedule, and last run.</summary>
public sealed class MaintenanceStatusDto
{
    public required bool IsRunning { get; init; }
    public required long DatabaseSizeBytes { get; init; }
    public required RetentionDto Retention { get; init; }
    public required double CleanupIntervalHours { get; init; }
    public required bool VacuumOnCleanup { get; init; }
    public CleanupResult? LastResult { get; init; }
}

/// <summary>Per-source packet retention windows, in days. 0 means keep forever ("off").</summary>
public sealed class RetentionDto
{
    public required int RfDays { get; init; }
    public required int AprsIsDays { get; init; }
    public required int OwnDays { get; init; }
}

public sealed class UpdateRetentionRequest
{
    public required int RfDays { get; init; }
    public required int AprsIsDays { get; init; }
    public required int OwnDays { get; init; }
}

/// <summary>
/// Optional criteria for a packet-reprocessing run. With an empty body, only rows below
/// the current parser version are re-derived (the usual "drain stale after a fix" case).
/// </summary>
public sealed class ReprocessRequest
{
    /// <summary>Re-derive matching rows regardless of their stored parser version.</summary>
    public bool Force { get; init; }

    /// <summary>Restrict to a single source: "Rf", "AprsIs", or "Own". Null = any.</summary>
    public string? Source { get; init; }

    /// <summary>Inclusive lower bound on ReceivedAt (UTC). Null = no lower bound.</summary>
    public DateTime? After { get; init; }

    /// <summary>Exclusive upper bound on ReceivedAt (UTC). Null = no upper bound.</summary>
    public DateTime? Before { get; init; }

    /// <summary>After the drain, delete stations left with no packets (excludes watch-listed).</summary>
    public bool DeleteOrphanStations { get; init; }
}

/// <summary>Progress and outcome of packet reprocessing.</summary>
public sealed class ReprocessStatusDto
{
    public required bool IsRunning { get; init; }
    public required long Processed { get; init; }
    public required long Total { get; init; }
    public required int CurrentParserVersion { get; init; }
    public ReprocessResult? LastResult { get; init; }
}
