using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/maintenance")]
public class MaintenanceController(
    DatabaseMaintenanceService maintenance,
    PacketReprocessingService reprocessor,
    StationSettingsProvider settingsProvider,
    DireControlContext db) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<MaintenanceStatusDto>> GetStatus(CancellationToken ct)
    {
        var row = await db.UserSettings.FindAsync([1], ct) ?? new UserSetting { Id = 1 };
        var effective = await settingsProvider.GetAsync(ct);

        return Ok(new MaintenanceStatusDto
        {
            IsRunning = maintenance.IsRunning,
            DatabaseSizeBytes = await maintenance.GetCurrentSizeBytesAsync(ct),
            Retention = new RetentionDto
            {
                RfDays = row.PacketRetentionRfDays,
                AprsIsDays = row.PacketRetentionAprsIsDays,
                OwnDays = row.PacketRetentionOwnDays,
            },
            CleanupIntervalHours = effective.DatabaseCleanupIntervalHours,
            VacuumOnCleanup = effective.VacuumOnCleanup,
            LastResult = maintenance.LastResult,
        });
    }

    [HttpPut("retention")]
    public async Task<ActionResult> UpdateRetention(
        [FromBody] UpdateRetentionRequest request,
        CancellationToken ct)
    {
        if (request.RfDays < 0 || request.AprsIsDays < 0 || request.OwnDays < 0)
            return BadRequest("Retention days cannot be negative. Use 0 to keep forever.");

        var setting = await db.UserSettings.FindAsync([1], ct);
        if (setting is null)
        {
            setting = new UserSetting { Id = 1 };
            db.UserSettings.Add(setting);
        }

        setting.PacketRetentionRfDays = request.RfDays;
        setting.PacketRetentionAprsIsDays = request.AprsIsDays;
        setting.PacketRetentionOwnDays = request.OwnDays;

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Triggers a cleanup run (prune + VACUUM) in the background.</summary>
    [HttpPost("cleanup")]
    public async Task<ActionResult> RunCleanup(CancellationToken ct)
    {
        if (!maintenance.TryStart((await settingsProvider.GetAsync(ct)).VacuumOnCleanup))
            return Conflict("A cleanup run is already in progress.");

        return Accepted();
    }

    /// <summary>Current packet-reprocessing progress and the last completed run.</summary>
    [HttpGet("reprocess")]
    public ActionResult<ReprocessStatusDto> GetReprocessStatus()
    {
        return Ok(new ReprocessStatusDto
        {
            IsRunning = reprocessor.IsRunning,
            Processed = reprocessor.Processed,
            Total = reprocessor.Total,
            CurrentParserVersion = ParserVersionInfo.Current,
            LastResult = reprocessor.LastResult,
        });
    }

    /// <summary>
    /// Triggers a packet-reprocessing run in the background. With an empty body it drains
    /// all rows below the current parser version; the optional filter narrows the scope.
    /// </summary>
    [HttpPost("reprocess")]
    public ActionResult RunReprocess([FromBody] ReprocessRequest? request)
    {
        request ??= new ReprocessRequest();

        PacketSource? source = null;
        if (!string.IsNullOrWhiteSpace(request.Source))
        {
            if (!Enum.TryParse<PacketSource>(request.Source, ignoreCase: true, out var parsed))
                return BadRequest($"Unknown source '{request.Source}'. Expected Rf, AprsIs, or Own.");
            source = parsed;
        }

        if (request.After is { } a && request.Before is { } b && a >= b)
            return BadRequest("'after' must be earlier than 'before'.");

        var filter = new ReprocessFilter
        {
            Force = request.Force,
            Source = source,
            After = request.After,
            Before = request.Before,
            DeleteOrphanStations = request.DeleteOrphanStations,
        };

        if (!reprocessor.TryStart(filter))
            return Conflict("A reprocessing run is already in progress.");

        return Accepted();
    }
}
