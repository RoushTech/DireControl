using DireControl.Api.Controllers.Models;
using DireControl.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/analysis")]
public class AnalysisController(DireControlContext db) : ControllerBase
{
    [HttpGet("coverage")]
    public async Task<ActionResult<IReadOnlyList<CoverageGridSquareDto>>> GetCoverage(CancellationToken ct)
    {
        var result = await db.CoverageGridStatistics
            .AsNoTracking()
            .OrderByDescending(c => c.PacketCount)
            .Select(c => new CoverageGridSquareDto
            {
                GridSquare = c.GridSquare,
                Lat = c.AvgLat,
                Lon = c.AvgLon,
                PacketCount = c.PacketCount,
            })
            .ToListAsync(ct);

        return Ok(result);
    }

    [HttpGet("digipeaters")]
    public async Task<ActionResult<IReadOnlyList<DigipeaterAnalysisEntryDto>>> GetDigipeaters(
        CancellationToken ct)
    {
        var result = await db.DigipeaterStatistics
            .AsNoTracking()
            .OrderByDescending(d => d.TotalPacketsForwarded)
            .Take(20)
            .Select(d => new DigipeaterAnalysisEntryDto
            {
                Callsign = d.Callsign,
                TotalPacketsForwarded = d.TotalPacketsForwarded,
                Last24h = d.Last24hPackets,
                AverageHopsFromUs = d.HopPositionCount > 0
                    ? d.HopPositionSum / d.HopPositionCount
                    : 0.0,
            })
            .ToListAsync(ct);

        return Ok(result);
    }
}
