using DireControl.Api.Services;

namespace DireControl.Api.Controllers.Models;

/// <summary>Current maintenance state: DB size, retention windows, schedule, and last run.</summary>
public sealed class MaintenanceStatusDto
{
    public required bool IsRunning { get; init; }
    public required long DatabaseSizeBytes { get; init; }
    public required RetentionDto Retention { get; init; }
    public required int CleanupIntervalHours { get; init; }
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
