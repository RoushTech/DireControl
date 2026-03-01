using DireControl.Api.Contracts;
using DireControl.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/alerts")]
public class AlertsController(DireControlContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AlertDto>>> GetAlerts(
        [FromQuery] bool? unacknowledged = null,
        CancellationToken ct = default)
    {
        var query = db.Alerts.AsNoTracking();

        if (unacknowledged == true)
            query = query.Where(a => !a.IsAcknowledged);

        var alerts = await query
            .OrderByDescending(a => a.TriggeredAt)
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
