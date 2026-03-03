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

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PacketDto>> GetById(int id, CancellationToken ct)
    {
        var p = await db.Packets
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new PacketDto
            {
                Id = x.Id,
                StationCallsign = x.StationCallsign,
                ReceivedAt = x.ReceivedAt,
                RawPacket = x.RawPacket,
                ParsedType = x.ParsedType,
                Latitude = x.Latitude,
                Longitude = x.Longitude,
                Path = x.Path,
                ResolvedPath = x.ResolvedPath,
                HopCount = x.HopCount,
                UnknownHopCount = x.UnknownHopCount,
                IsDirectHeard = x.HopCount == 0,
                Comment = x.Comment,
                WeatherData = x.WeatherData,
                TelemetryData = x.TelemetryData,
                MessageData = x.MessageData,
                SignalData = x.SignalData,
                GridSquare = x.GridSquare,
            })
            .FirstOrDefaultAsync(ct);

        if (p is null) return NotFound();
        return Ok(p);
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
                IsDirectHeard = p.HopCount == 0,
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
                IsDirectHeard = p.HopCount == 0,
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
