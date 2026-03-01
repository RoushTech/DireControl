using DireControl.Api.Contracts;
using DireControl.Data;
using DireControl.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/geofences")]
public class GeofencesController(DireControlContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GeofenceDto>>> GetGeofences(CancellationToken ct)
    {
        var fences = await db.Geofences
            .AsNoTracking()
            .OrderBy(f => f.Name)
            .Select(f => new GeofenceDto
            {
                Id = f.Id,
                Name = f.Name,
                CenterLat = f.CenterLat,
                CenterLon = f.CenterLon,
                RadiusMeters = f.RadiusMeters,
                IsActive = f.IsActive,
                AlertOnEnter = f.AlertOnEnter,
                AlertOnExit = f.AlertOnExit,
            })
            .ToListAsync(ct);

        return Ok(fences);
    }

    [HttpPost]
    public async Task<ActionResult<GeofenceDto>> CreateGeofence(
        [FromBody] CreateGeofenceRequest request,
        CancellationToken ct)
    {
        var fence = new Geofence
        {
            Name = request.Name,
            CenterLat = request.CenterLat,
            CenterLon = request.CenterLon,
            RadiusMeters = request.RadiusMeters,
            IsActive = true,
            AlertOnEnter = request.AlertOnEnter,
            AlertOnExit = request.AlertOnExit,
        };

        db.Geofences.Add(fence);
        await db.SaveChangesAsync(ct);

        var dto = new GeofenceDto
        {
            Id = fence.Id,
            Name = fence.Name,
            CenterLat = fence.CenterLat,
            CenterLon = fence.CenterLon,
            RadiusMeters = fence.RadiusMeters,
            IsActive = fence.IsActive,
            AlertOnEnter = fence.AlertOnEnter,
            AlertOnExit = fence.AlertOnExit,
        };

        return CreatedAtAction(nameof(GetGeofences), dto);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteGeofence(int id, CancellationToken ct)
    {
        var fence = await db.Geofences.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (fence is null) return NotFound();

        db.Geofences.Remove(fence);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
