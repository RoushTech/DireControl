using DireControl.Api.Controllers.Models;
using DireControl.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/alerts")]
public class AlertsController(DireControlContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AlertDto>>> GetAlerts(
        [FromQuery] bool? unacknowledged = null,
        [FromQuery] int limit = 500,
        CancellationToken ct = default)
    {
        // Alerts accrue indefinitely; cap what one request can pull.
        limit = Math.Clamp(limit, 1, 2000);

        var query = db.Alerts.AsNoTracking();

        if (unacknowledged == true)
            query = query.Where(a => !a.IsAcknowledged);

        var alerts = await query
            .OrderByDescending(a => a.TriggeredAt)
            .Take(limit)
            .ToListAsync(ct);

        var dtos = alerts.Select(a => new AlertDto
        {
            Id = a.Id,
            AlertType = (int)a.AlertType,
            AlertTypeName = a.AlertType.ToString(),
            Callsign = a.Callsign,
            TriggeredAt = a.TriggeredAt,
            IsAcknowledged = a.IsAcknowledged,
            DistanceMeters = a.Detail.DistanceMeters,
            GeofenceName = a.Detail.GeofenceName,
            Direction = a.Detail.Direction,
            RuleName = a.Detail.RuleName,
            MessageText = a.Detail.MessageText,
        }).ToList();

        return Ok(dtos);
    }

    [HttpPut("{id:int}/acknowledge")]
    public async Task<IActionResult> AcknowledgeAlert(int id, CancellationToken ct)
    {
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (alert is null) return NotFound();

        alert.IsAcknowledged = true;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
