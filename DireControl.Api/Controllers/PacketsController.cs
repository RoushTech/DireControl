using DireControl.Api.Controllers.Models;
using DireControl.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/packets")]
public class PacketsController(DireControlContext db) : ControllerBase
{
    [HttpGet("positions")]
    public async Task<ActionResult<IReadOnlyList<PacketPositionDto>>> GetPositions(
        [FromQuery] int hours = 24,
        CancellationToken ct = default)
    {
        // Time-bounded so the heatmap doesn't pull the entire position history
        // on every toggle; the row cap is a hard backstop.
        hours = Math.Clamp(hours, 1, 24 * 30);
        var since = DateTime.UtcNow.AddHours(-hours);

        var positions = await db.Packets
            .AsNoTracking()
            .Where(p => p.Latitude != null && p.Longitude != null && p.ReceivedAt >= since)
            .OrderByDescending(p => p.ReceivedAt)
            .Take(50_000)
            .Select(p => new PacketPositionDto
            {
                Latitude = p.Latitude!.Value,
                Longitude = p.Longitude!.Value,
            })
            .ToListAsync(ct);

        return Ok(positions);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PacketDto>> GetById(int id, CancellationToken ct)
    {
        var p = await db.Packets
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(PacketProjections.ToPacketDto)
            .FirstOrDefaultAsync(ct);

        if (p is null) return NotFound();
        return Ok(p);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PacketDto>>> GetSince(
        [FromQuery] DateTime? since,
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        var query = db.Packets.AsNoTracking();

        if (since.HasValue)
        {
            var sinceUtc = since.Value.Kind == DateTimeKind.Utc
                ? since.Value
                : since.Value.ToUniversalTime();
            query = query.Where(p => p.ReceivedAt >= sinceUtc);
        }

        var packets = await query
            .OrderBy(p => p.ReceivedAt)
            .Take(Math.Min(limit, 500))
            .Select(PacketProjections.ToPacketDto)
            .ToListAsync(ct);

        return Ok(packets);
    }
}
