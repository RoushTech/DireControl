using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using DireControl.Data;
using DireControl.Data.Models;
using DireControl.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DireControl.Api.Controllers;

/// <summary>Sysop view of the PMS mailbox: browse, compose, kill, delete.</summary>
[ApiController]
[Route("api/v0/pms")]
public class PmsController(
    DireControlContext db,
    IOptions<DireControlOptions> options) : ControllerBase
{
    [HttpGet("messages")]
    public async Task<ActionResult<PmsMessagePageDto>> ListMessages(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] bool includeKilled = false,
        CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.PmsMessages.AsNoTracking();
        if (!includeKilled)
            query = query.Where(m => !m.IsKilled);

        var total = await query.CountAsync(ct);
        var messages = await query
            .OrderByDescending(m => m.Id)
            .Skip((Math.Max(page, 1) - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PmsMessagePageDto
        {
            TotalCount = total,
            Messages = messages.Select(ToDto).ToList(),
        });
    }

    [HttpPost("messages")]
    public async Task<ActionResult<PmsMessageDto>> Compose(
        [FromBody] ComposePmsMessageRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ToCallsign))
            return BadRequest("Addressee is required.");
        if (request.Type is not (PmsMessageType.Private or PmsMessageType.Bulletin))
            return BadRequest("Type must be Private or Bulletin.");

        var stationBase = options.Value.OurCallsign.Split('-')[0].ToUpperInvariant();
        var message = new PmsMessage
        {
            Type = request.Type,
            FromCallsign = stationBase,
            ToCallsign = request.ToCallsign.Trim().ToUpperInvariant(),
            Subject = request.Subject?.Trim() ?? string.Empty,
            Body = request.Body ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            Origin = TerminalSessionOrigin.Unknown, // sysop web compose
        };
        db.PmsMessages.Add(message);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(message));
    }

    [HttpPost("messages/{id:int}/kill")]
    public async Task<ActionResult> Kill(int id, CancellationToken ct)
    {
        var message = await db.PmsMessages.FindAsync([id], ct);
        if (message is null)
            return NotFound();
        if (!message.IsKilled)
        {
            message.IsKilled = true;
            message.KilledAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    [HttpDelete("messages/{id:int}")]
    public async Task<ActionResult> Delete(int id, CancellationToken ct)
    {
        var message = await db.PmsMessages.FindAsync([id], ct);
        if (message is null)
            return NotFound();
        db.PmsMessages.Remove(message);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static PmsMessageDto ToDto(PmsMessage m) => new()
    {
        Id = m.Id,
        Type = m.Type,
        FromCallsign = m.FromCallsign,
        ToCallsign = m.ToCallsign,
        Subject = m.Subject,
        Body = m.Body,
        CreatedAt = m.CreatedAt,
        ReadAt = m.ReadAt,
        IsKilled = m.IsKilled,
        Origin = m.Origin,
    };
}
