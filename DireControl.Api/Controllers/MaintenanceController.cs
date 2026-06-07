using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/maintenance")]
public class MaintenanceController(
    DatabaseMaintenanceService maintenance,
    IOptions<DireControlOptions> options,
    DireControlContext db) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<MaintenanceStatusDto>> GetStatus(CancellationToken ct)
    {
        var settings = await db.UserSettings.FindAsync([1], ct) ?? new UserSetting { Id = 1 };

        return Ok(new MaintenanceStatusDto
        {
            IsRunning = maintenance.IsRunning,
            DatabaseSizeBytes = await maintenance.GetCurrentSizeBytesAsync(ct),
            Retention = new RetentionDto
            {
                RfDays = settings.PacketRetentionRfDays,
                AprsIsDays = settings.PacketRetentionAprsIsDays,
                OwnDays = settings.PacketRetentionOwnDays,
            },
            CleanupIntervalHours = options.Value.DatabaseCleanupIntervalHours,
            VacuumOnCleanup = options.Value.VacuumOnCleanup,
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
    public ActionResult RunCleanup()
    {
        if (!maintenance.TryStart(options.Value.VacuumOnCleanup))
            return Conflict("A cleanup run is already in progress.");

        return Accepted();
    }
}
