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
    public async Task<ActionResult<IReadOnlyList<PacketPositionDto>>> GetPositions(CancellationToken ct)
    {
        var positions = await db.Packets
            .AsNoTracking()
            .Where(p => p.Latitude != null && p.Longitude != null)
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

    [HttpGet("recent")]
    public async Task<ActionResult<IReadOnlyList<PacketDto>>> GetRecent(CancellationToken ct)
    {
        var packets = await db.Packets
            .AsNoTracking()
            .OrderByDescending(p => p.ReceivedAt)
            .Take(100)
            .Select(p => new PacketDto
            {
                Id = p.Id,
                StationCallsign = p.StationCallsign,
                ReceivedAt = p.ReceivedAt,
                RawPacket = p.RawPacket,
                ParsedType = p.ParsedType,
                Latitude = p.Latitude,
                Longitude = p.Longitude,
                Path = p.Path,
                ResolvedPath = p.ResolvedPath,
                HopCount = p.HopCount,
                UnknownHopCount = p.UnknownHopCount,
                Comment = p.Comment,
                WeatherData = p.WeatherData,
                TelemetryData = p.TelemetryData,
                MessageData = p.MessageData,
                SignalData = p.SignalData,
                GridSquare = p.GridSquare,
            })
            .ToListAsync(ct);

        return Ok(packets);
    }
}
